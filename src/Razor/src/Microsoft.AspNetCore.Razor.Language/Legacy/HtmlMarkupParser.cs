﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.AspNetCore.Razor.Language.Syntax.InternalSyntax;

namespace Microsoft.AspNetCore.Razor.Language.Legacy
{
    internal partial class HtmlMarkupParser : TokenizerBackedParser<HtmlTokenizer>
    {
        private const string ScriptTagName = "script";
        private static readonly SyntaxList<RazorSyntaxNode> EmptySyntaxList = new SyntaxListBuilder<RazorSyntaxNode>(0).ToList();

        private static readonly char[] ValidAfterTypeAttributeNameCharacters = { ' ', '\t', '\r', '\n', '\f', '=' };
        private static readonly SyntaxToken[] nonAllowedHtmlCommentEnding = new[]
        {
            SyntaxFactory.Token(SyntaxKind.Text, "-"),
            SyntaxFactory.Token(SyntaxKind.Bang, "!"),
            SyntaxFactory.Token(SyntaxKind.OpenAngle, "<"),
        };

        private Stack<TagTracker> _tagTracker = new Stack<TagTracker>();

        public HtmlMarkupParser(ParserContext context)
            : base(context.ParseLeadingDirectives ? FirstDirectiveHtmlLanguageCharacteristics.Instance : HtmlLanguageCharacteristics.Instance, context)
        {
        }

        private TagTracker CurrentTracker => _tagTracker.Count > 0 ? _tagTracker.Peek() : null;

        private string CurrentStartTagName => CurrentTracker?.TagName;

        private SourceLocation CurrentStartTagLocation => CurrentTracker?.TagLocation ?? SourceLocation.Undefined;

        public CSharpCodeParser CodeParser { get; set; }

        public RazorDocumentSyntax ParseDocument()
        {
            if (Context == null)
            {
                throw new InvalidOperationException(Resources.Parser_Context_Not_Set);
            }

            using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
            using (PushSpanContextConfig(DefaultMarkupSpanContext))
            {
                var builder = pooledResult.Builder;
                NextToken();

                ParseMarkupNodes(builder, ParseMode.Markup);
                AcceptMarkerTokenIfNecessary();
                builder.Add(OutputAsMarkupLiteral());

                // If we are still tracking any unclosed start tags, we need to close them.
                while (_tagTracker.Count > 0)
                {
                    var tracker = _tagTracker.Pop();
                    var element = SyntaxFactory.MarkupElement(tracker.StartTag, builder.Consume(), endTag: null);
                    builder.AddRange(tracker.PreviousNodes);
                    builder.Add(element);
                }

                var markup = SyntaxFactory.MarkupBlock(builder.ToList());

                return SyntaxFactory.RazorDocument(markup);
            }
        }

        public MarkupBlockSyntax ParseBlock()
        {
            if (Context == null)
            {
                throw new InvalidOperationException(Resources.Parser_Context_Not_Set);
            }

            var oldTagTracker = _tagTracker;
            try
            {
                _tagTracker = new Stack<TagTracker>();
                using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
                using (PushSpanContextConfig(DefaultMarkupSpanContext))
                {
                    var builder = pooledResult.Builder;
                    if (!NextToken())
                    {
                        return null;
                    }

                    ParseMarkupInCodeBlock(builder);
                    builder.Add(OutputAsMarkupLiteral());

                    var markupBlock = builder.ToList();

                    return SyntaxFactory.MarkupBlock(markupBlock);
                }
            }
            finally
            {
                _tagTracker = oldTagTracker;
            }
        }

        private void ParseMarkupNodes(
            in SyntaxListBuilder<RazorSyntaxNode> builder,
            ParseMode mode,
            Func<SyntaxToken, bool> stopCondition = null)
        {
            stopCondition = stopCondition ?? (token => false);
            while (!EndOfFile && !stopCondition(CurrentToken))
            {
                ParseMarkupNode(builder, mode);
            }
        }

        private void ParseMarkupNode(in SyntaxListBuilder<RazorSyntaxNode> builder, ParseMode mode)
        {
            switch (GetParserState(mode))
            {
                case ParserState.MarkupText:
                    ParseMarkupText(builder);
                    break;
                case ParserState.Tag:
                    ParseMarkupElement(builder, mode);
                    break;
                case ParserState.SpecialTag:
                    ParseSpecialTag(builder);
                    break;
                case ParserState.XmlPI:
                    ParseXmlPI(builder);
                    break;
                case ParserState.CData:
                    ParseCData(builder);
                    break;
                case ParserState.MarkupComment:
                    ParseMarkupComment(builder);
                    break;
                case ParserState.RazorComment:
                    ParseRazorCommentWithLeadingAndTrailingWhitespace(builder);
                    break;
                case ParserState.DoubleTransition:
                    ParseDoubleTransition(builder);
                    break;
                case ParserState.CodeTransition:
                    ParseCodeTransition(builder);
                    break;
                case ParserState.Misc:
                    ParseMisc(builder);
                    break;
                case ParserState.Unknown:
                    AcceptAndMoveNext();
                    break;
                case ParserState.EOF:
                    builder.Add(OutputAsMarkupLiteral());
                    break;
            }
        }

        private void ParseMarkupText(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            AcceptAndMoveNext();
        }

        private void ParseMarkupInCodeBlock(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            do
            {
                ParseMarkupNodes(builder, ParseMode.Text, token => token.Kind == SyntaxKind.OpenAngle);
                if (EndOfFile)
                {
                    CompleteMarkupInCodeBlock(builder);
                    return;
                }

                Assert(SyntaxKind.OpenAngle);
                ParseMarkupElement(builder, ParseMode.MarkupInCodeBlock);
            } while (_tagTracker.Count > 0);
        }

        private void CompleteMarkupInCodeBlock(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            while (_tagTracker.Count > 0)
            {
                var tracker = _tagTracker.Pop();
                var element = SyntaxFactory.MarkupElement(tracker.StartTag, builder.Consume(), endTag: null);
                builder.AddRange(tracker.PreviousNodes);
                builder.Add(element);

                if (_tagTracker.Count == 0)
                {
                    // We're at the outermost start tag. Add an error.
                    Context.ErrorSink.OnError(
                        RazorDiagnosticFactory.CreateParsing_MissingEndTag(
                            new SourceSpan(
                                SourceLocationTracker.Advance(tracker.TagLocation, "<"),
                                tracker.TagName.Length),
                            tracker.TagName));
                }
            }

            if (!Context.DesignTimeMode)
            {
                // Do some whitespace handling
            }
        }

        private void ParseMarkupElement(in SyntaxListBuilder<RazorSyntaxNode> builder, ParseMode mode)
        {
            Assert(SyntaxKind.OpenAngle);

            // Output already accepted tokens if any.
            builder.Add(OutputAsMarkupLiteral());

            if (!NextIs(SyntaxKind.ForwardSlash))
            {
                // Parsing a start tag
                var tagStart = CurrentStart;
                var startTag = ParseStartTag(mode, tagStart, out var tagName, out var tagMode);
                if (tagMode == MarkupTagMode.Script)
                {
                    var acceptedCharacters = mode == ParseMode.MarkupInCodeBlock ? AcceptedCharactersInternal.None : AcceptedCharactersInternal.Any;
                    ParseJavascriptAndEndScriptTag(builder, startTag, acceptedCharacters);
                    return;
                }
                else if (tagMode == MarkupTagMode.SelfClosing || tagMode == MarkupTagMode.Invalid)
                {
                    // For cases like <foo /> or invalid cases like |<|<p>
                    var element = SyntaxFactory.MarkupElement(startTag, EmptySyntaxList, endTag: null);
                    builder.Add(element);
                    return;
                }
                else
                {
                    // This is a normal start tag. We need to keep track of it.
                    var tracker = new TagTracker(tagName, startTag, tagStart, builder.Consume());
                    _tagTracker.Push(tracker);
                    return;
                }
            }
            else
            {
                // Parsing an end tag.
                var endTagStart = CurrentStart;
                var endTag = ParseEndTag(mode, out var endTagName);
                if (endTagName != null && string.Equals(CurrentStartTagName, endTagName, StringComparison.OrdinalIgnoreCase))
                {
                    // Happy path. Found a matching start tag. Create the element and reset the builder.
                    var tracker = _tagTracker.Pop();
                    var element = SyntaxFactory.MarkupElement(tracker.StartTag, builder.Consume(), endTag);
                    builder.AddRange(tracker.PreviousNodes);
                    builder.Add(element);
                    return;
                }
                else if (mode == ParseMode.MarkupInCodeBlock)
                {
                    // Try to recover the start tag in the stack
                    // Since we're in a code block, if we couldn't find a matching start tag, 
                    // we should just add an error and exit.
                    if (!TryRecoverStartTagInCodeBlock(builder, endTagName, endTagStart, endTag))
                    {
                        var element = SyntaxFactory.MarkupElement(startTag: null, body: EmptySyntaxList, endTag: endTag);
                        builder.Add(element);
                    }
                }
                else
                {
                    // Current tag scope does not match the end tag. Attempt to recover the start tag
                    // by looking up the previous tag scopes for a matching start tag.
                    if (!TryRecoverStartTag(builder, endTagName, endTag))
                    {
                        // Could not recover.
                        var element = SyntaxFactory.MarkupElement(startTag: null, body: EmptySyntaxList, endTag: endTag);
                        builder.Add(element);
                    }
                }
            }
        }

        private bool TryRecoverStartTagInCodeBlock(
            in SyntaxListBuilder<RazorSyntaxNode> builder,
            string endTagName,
            SourceLocation endTagStartLocation,
            MarkupEndTagSyntax endTag)
        {
            if (_tagTracker.Count == 0)
            {
                // We can't possibly have a matching start tag.
                Context.ErrorSink.OnError(
                    RazorDiagnosticFactory.CreateParsing_UnexpectedEndTag(
                        new SourceSpan(SourceLocationTracker.Advance(endTagStartLocation, "</"), endTagName.Length), endTagName));
                return false;
            }

            while (_tagTracker.Count > 0)
            {
                var tracker = _tagTracker.Pop();
                if (string.Equals(tracker.TagName, endTagName, StringComparison.OrdinalIgnoreCase))
                {
                    // Found a match
                    var element = SyntaxFactory.MarkupElement(tracker.StartTag, builder.Consume(), endTag);
                    builder.AddRange(tracker.PreviousNodes);
                    builder.Add(element);
                    return true;
                }

                var unclosedElement = SyntaxFactory.MarkupElement(tracker.StartTag, builder.Consume(), endTag: null);
                builder.AddRange(tracker.PreviousNodes);
                builder.Add(unclosedElement);

                if (_tagTracker.Count == 0)
                {
                    // This means we couldn't find a match and we're at the outermost start tag. Add an error.
                    Context.ErrorSink.OnError(
                        RazorDiagnosticFactory.CreateParsing_MissingEndTag(
                            new SourceSpan(
                                SourceLocationTracker.Advance(tracker.TagLocation, "<"),
                                tracker.TagName.Length),
                            tracker.TagName));
                }
            }

            return false;
        }

        private bool TryRecoverStartTag(in SyntaxListBuilder<RazorSyntaxNode> builder, string endTagName, MarkupEndTagSyntax endTag)
        {
            var malformedTagCount = 0;
            foreach (var tag in _tagTracker)
            {
                if (string.Equals(tag.TagName, endTagName, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                malformedTagCount++;
            }

            if (malformedTagCount != _tagTracker.Count)
            {
                // This means we found a matching tag.
                for (var i = 0; i < malformedTagCount; i++)
                {
                    var tracker = _tagTracker.Pop();
                    var malformedElement = SyntaxFactory.MarkupElement(tracker.StartTag, builder.Consume(), endTag: null);
                    builder.AddRange(tracker.PreviousNodes);
                    builder.Add(malformedElement);
                }

                // Now complete our target tag which is not malformed.
                var tagTracker = _tagTracker.Pop();
                var element = SyntaxFactory.MarkupElement(tagTracker.StartTag, builder.Consume(), endTag);
                builder.AddRange(tagTracker.PreviousNodes);
                builder.Add(element);

                return true;
            }

            return false;
        }

        private MarkupStartTagSyntax ParseStartTag(ParseMode mode, SourceLocation tagStartLocation, out string tagName, out MarkupTagMode tagMode)
        {
            tagName = null;
            tagMode = MarkupTagMode.Invalid;
            using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
            {
                var tagBuilder = pooledResult.Builder;

                AcceptAndMoveNext(); // Accept '<'
                var isBangEscape = TryParseBangEscape(tagBuilder);

                if (At(SyntaxKind.Text))
                {
                    tagName = CurrentToken.Content;
                    if (ParserHelpers.VoidElements.Contains(tagName))
                    {
                        // This is a void element.
                        tagMode = MarkupTagMode.Void;
                    }
                    else
                    {
                        tagMode = MarkupTagMode.Normal;
                    }

                    if (isBangEscape)
                    {
                        // We don't want to group <p> and </!p> together.
                        tagName = "!" + tagName;
                    }
                }
                TryAccept(SyntaxKind.Text);

                if (At(SyntaxKind.CloseAngle) && mode == ParseMode.MarkupInCodeBlock)
                {
                    // Completed tags in code blocks have no accepted characters.
                    SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                }

                // Output open angle and tag name
                tagBuilder.Add(OutputAsMarkupLiteral());


                // Parse the contents of a tag like attributes.
                ParseAttributes(tagBuilder);

                if (TryAccept(SyntaxKind.ForwardSlash))
                {
                    // This is a self closing tag.
                    tagMode = MarkupTagMode.SelfClosing;
                }
                if (!TryAccept(SyntaxKind.CloseAngle) && mode == ParseMode.MarkupInCodeBlock)
                {
                    Context.ErrorSink.OnError(
                    RazorDiagnosticFactory.CreateParsing_UnfinishedTag(
                        new SourceSpan(
                            SourceLocationTracker.Advance(tagStartLocation, "<"),
                            Math.Max(tagName.Length, 1)),
                        tagName));
                }
                else if (mode == ParseMode.MarkupInCodeBlock)
                {
                    // Completed tags in code blocks have no accepted characters.
                    SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                }

                // End tag block
                tagBuilder.Add(OutputAsMarkupLiteral());
                var tagBlock = SyntaxFactory.MarkupStartTag(tagBuilder.ToList());
                if (string.Equals(tagName, ScriptTagName, StringComparison.OrdinalIgnoreCase))
                {
                    // If the script tag expects javascript content then we should do minimal parsing until we reach
                    // the end script tag. Don't want to incorrectly parse a "var tag = '<input />';" as an HTML tag.
                    if (!ScriptTagExpectsHtml(tagBlock))
                    {
                        tagMode = MarkupTagMode.Script;
                    }
                }

                return tagBlock;
            }
        }

        private MarkupEndTagSyntax ParseEndTag(ParseMode mode, out string tagName)
        {
            // This section can accept things like: '</p  >' or '</p>' etc.
            Assert(SyntaxKind.OpenAngle);

            tagName = null;

            using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
            {
                var tagBuilder = pooledResult.Builder;

                AcceptAndMoveNext(); // Accept '<'
                TryAccept(SyntaxKind.ForwardSlash);

                // Whitespace here is invalid (according to the spec)
                var isBangEscape = TryParseBangEscape(tagBuilder);
                if (At(SyntaxKind.Text))
                {
                    tagName = isBangEscape ? "!" : string.Empty;
                    tagName += CurrentToken.Content;
                    AcceptAndMoveNext();
                }
                TryAccept(SyntaxKind.Whitespace);
                if (TryAccept(SyntaxKind.CloseAngle) && mode == ParseMode.MarkupInCodeBlock)
                {
                    // Completed tags in code blocks have no accepted characters.
                    SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                }

                // End tag block
                tagBuilder.Add(OutputAsMarkupLiteral());
                var tagBlock = SyntaxFactory.MarkupEndTag(tagBuilder.ToList());
                return tagBlock;
            }
        }

        private void ParseAttributes(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            if (!At(SyntaxKind.Whitespace) && !At(SyntaxKind.NewLine))
            {
                // We should be right after the tag name, so if there's no whitespace or new line, something is wrong
                ParseMiscAttribute(builder);
                return;
            }

            // We are here ($): <tag$ foo="bar" biz="~/Baz" />

            while (!EndOfFile && !IsEndOfTag())
            {
                if (At(SyntaxKind.ForwardSlash))
                {
                    // This means we're at a '/' but it's not considered end of tag. E.g. <p / class=foo>
                    // We are at the '/' but the tag isn't closed. Accept and continue parsing the next attribute.
                    AcceptAndMoveNext();
                }

                ParseAttribute(builder);
            }
        }

        private bool IsEndOfTag()
        {
            if (At(SyntaxKind.ForwardSlash))
            {
                if (NextIs(SyntaxKind.CloseAngle) || NextIs(SyntaxKind.OpenAngle))
                {
                    return true;
                }
            }

            return At(SyntaxKind.CloseAngle) || At(SyntaxKind.OpenAngle);
        }

        private void ParseMiscAttribute(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
            {
                var miscAttributeContentBuilder = pooledResult.Builder;
                while (!EndOfFile)
                {
                    ParseMarkupNodes(miscAttributeContentBuilder, ParseMode.Text, IsTagRecoveryStopPoint);
                    if (!EndOfFile)
                    {
                        EnsureCurrent();
                        switch (CurrentToken.Kind)
                        {
                            case SyntaxKind.SingleQuote:
                            case SyntaxKind.DoubleQuote:
                                // We should parse until we reach a matching quote.
                                var openQuoteKind = CurrentToken.Kind;
                                AcceptAndMoveNext();
                                ParseMarkupNodes(miscAttributeContentBuilder, ParseMode.Text, token => token.Kind == openQuoteKind);
                                if (!EndOfFile)
                                {
                                    Assert(openQuoteKind);
                                    AcceptAndMoveNext();
                                }
                                break;
                            case SyntaxKind.OpenAngle: // Another "<" means this tag is invalid.
                            case SyntaxKind.ForwardSlash: // Empty tag
                            case SyntaxKind.CloseAngle: // End of tag
                                miscAttributeContentBuilder.Add(OutputAsMarkupLiteral());
                                if (miscAttributeContentBuilder.Count > 0)
                                {
                                    var miscAttributeContent = SyntaxFactory.MarkupMiscAttributeContent(miscAttributeContentBuilder.ToList());
                                    builder.Add(miscAttributeContent);
                                }
                                return;
                            default:
                                AcceptAndMoveNext();
                                break;
                        }
                    }
                }

                miscAttributeContentBuilder.Add(OutputAsMarkupLiteral());
                if (miscAttributeContentBuilder.Count > 0)
                {
                    var miscAttributeContent = SyntaxFactory.MarkupMiscAttributeContent(miscAttributeContentBuilder.ToList());
                    builder.Add(miscAttributeContent);
                }
            }
        }

        private void ParseAttribute(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            // Output anything prior to the attribute, in most cases this will be any invalid content after the tag name or a previous attribute:
            // <input| /| checked />. If there is nothing in-between other attributes this will noop.
            using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
            {
                var miscAttributeContentBuilder = pooledResult.Builder;
                miscAttributeContentBuilder.Add(OutputAsMarkupLiteral());
                if (miscAttributeContentBuilder.Count > 0)
                {
                    var invalidAttributeBlock = SyntaxFactory.MarkupMiscAttributeContent(miscAttributeContentBuilder.ToList());
                    builder.Add(invalidAttributeBlock);
                }
            }

            // http://dev.w3.org/html5/spec/tokenization.html#before-attribute-name-state
            // Capture whitespace
            var attributePrefixWhitespace = ReadWhile(token => token.Kind == SyntaxKind.Whitespace || token.Kind == SyntaxKind.NewLine);

            // http://dev.w3.org/html5/spec/tokenization.html#attribute-name-state
            // Read the 'name' (i.e. read until the '=' or whitespace/newline)
            if (!TryParseAttributeName(out var nameTokens))
            {
                // Unexpected character in tag, enter recovery
                Accept(attributePrefixWhitespace);
                ParseMiscAttribute(builder);
                return;
            }

            Accept(attributePrefixWhitespace); // Whitespace before attribute name
            var namePrefix = OutputAsMarkupLiteral();
            Accept(nameTokens); // Attribute name
            var name = OutputAsMarkupLiteral();

            var atMinimizedAttribute = !TokenExistsAfterWhitespace(SyntaxKind.Equals);
            if (atMinimizedAttribute)
            {
                // Minimized attribute
                var minimizedAttributeBlock = SyntaxFactory.MarkupMinimizedAttributeBlock(namePrefix, name);
                builder.Add(minimizedAttributeBlock);
            }
            else
            {
                // Not a minimized attribute
                var attributeBlock = ParseRemainingAttribute(namePrefix, name);
                builder.Add(attributeBlock);
            }
        }

        private bool TryParseAttributeName(out IEnumerable<SyntaxToken> nameTokens)
        {
            nameTokens = Enumerable.Empty<SyntaxToken>();
            if (At(SyntaxKind.Transition) || At(SyntaxKind.RazorCommentTransition))
            {
                return false;
            }

            if (IsValidAttributeNameToken(CurrentToken))
            {
                nameTokens = ReadWhile(token =>
                    token.Kind != SyntaxKind.Whitespace &&
                    token.Kind != SyntaxKind.NewLine &&
                    token.Kind != SyntaxKind.Equals &&
                    token.Kind != SyntaxKind.CloseAngle &&
                    token.Kind != SyntaxKind.OpenAngle &&
                    (token.Kind != SyntaxKind.ForwardSlash || !NextIs(SyntaxKind.CloseAngle)));

                return true;
            }

            return false;
        }

        private MarkupAttributeBlockSyntax ParseRemainingAttribute(MarkupTextLiteralSyntax namePrefix, MarkupTextLiteralSyntax name)
        {
            // Since this is not a minimized attribute, the whitespace after attribute name belongs to this attribute.
            AcceptWhile(token => token.Kind == SyntaxKind.Whitespace || token.Kind == SyntaxKind.NewLine);
            var nameSuffix = OutputAsMarkupLiteral();

            Assert(SyntaxKind.Equals); // We should be at "="
            var equalsToken = EatCurrentToken();

            var whitespaceAfterEquals = ReadWhile(token => token.Kind == SyntaxKind.Whitespace || token.Kind == SyntaxKind.NewLine);
            var quote = SyntaxKind.Marker;
            if (At(SyntaxKind.SingleQuote) || At(SyntaxKind.DoubleQuote))
            {
                // Found a quote, the whitespace belongs to this attribute.
                Accept(whitespaceAfterEquals);
                quote = CurrentToken.Kind;
                AcceptAndMoveNext();
            }
            else if (whitespaceAfterEquals.Any())
            {
                // No quotes found after the whitespace. Put it back so that it can be parsed later.
                PutCurrentBack();
                PutBack(whitespaceAfterEquals);
            }

            MarkupTextLiteralSyntax valuePrefix = null;
            RazorBlockSyntax attributeValue = null;
            MarkupTextLiteralSyntax valueSuffix = null;

            // First, determine if this is a 'data-' attribute (since those can't use conditional attributes)
            var nameContent = string.Concat(name.LiteralTokens.Nodes.Select(s => s.Content));
            if (IsConditionalAttributeName(nameContent))
            {
                SpanContext.ChunkGenerator = SpanChunkGenerator.Null; // The block chunk generator will render the prefix

                // We now have the value prefix which is usually whitespace and/or a quote
                valuePrefix = OutputAsMarkupLiteral();

                // Read the attribute value only if the value is quoted
                // or if there is no whitespace between '=' and the unquoted value.
                if (quote != SyntaxKind.Marker || !whitespaceAfterEquals.Any())
                {
                    using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
                    {
                        var attributeValueBuilder = pooledResult.Builder;
                        // Read the attribute value.
                        while (!EndOfFile && !IsEndOfAttributeValue(quote, CurrentToken))
                        {
                            ParseConditionalAttributeValue(attributeValueBuilder, quote);
                        }

                        if (attributeValueBuilder.Count > 0)
                        {
                            attributeValue = SyntaxFactory.GenericBlock(attributeValueBuilder.ToList());
                        }
                    }
                }

                // Capture the suffix
                if (quote != SyntaxKind.Marker && At(quote))
                {
                    AcceptAndMoveNext();
                    // Again, block chunk generator will render the suffix
                    SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                    valueSuffix = OutputAsMarkupLiteral();
                }
            }
            else if (quote != SyntaxKind.Marker || !whitespaceAfterEquals.Any())
            {
                valuePrefix = OutputAsMarkupLiteral();

                attributeValue = ParseNonConditionalAttributeValue(quote);

                if (quote != SyntaxKind.Marker)
                {
                    TryAccept(quote);
                    valueSuffix = OutputAsMarkupLiteral();
                }
            }
            else
            {
                // There is no quote and there is whitespace after equals. There is no attribute value.
            }

            return SyntaxFactory.MarkupAttributeBlock(namePrefix, name, nameSuffix, equalsToken, valuePrefix, attributeValue, valueSuffix);
        }

        private RazorBlockSyntax ParseNonConditionalAttributeValue(SyntaxKind quote)
        {
            using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
            {
                var attributeValueBuilder = pooledResult.Builder;
                // Not a "conditional" attribute, so just read the value
                ParseMarkupNodes(attributeValueBuilder, ParseMode.Text, token => IsEndOfAttributeValue(quote, token));

                // Output already accepted tokens if any as markup literal
                var literalValue = OutputAsMarkupLiteral();
                attributeValueBuilder.Add(literalValue);

                // Capture the attribute value (will include everything in-between the attribute's quotes).
                return SyntaxFactory.GenericBlock(attributeValueBuilder.ToList());
            }
        }

        private void ParseConditionalAttributeValue(in SyntaxListBuilder<RazorSyntaxNode> builder, SyntaxKind quote)
        {
            var prefixStart = CurrentStart;
            var prefixTokens = ReadWhile(token => token.Kind == SyntaxKind.Whitespace || token.Kind == SyntaxKind.NewLine);

            if (At(SyntaxKind.Transition))
            {
                if (NextIs(SyntaxKind.Transition))
                {
                    using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
                    {
                        var markupBuilder = pooledResult.Builder;
                        Accept(prefixTokens);

                        // Render a single "@" in place of "@@".
                        SpanContext.ChunkGenerator = new LiteralAttributeChunkGenerator(
                            new LocationTagged<string>(string.Concat(prefixTokens.Select(s => s.Content)), prefixStart),
                            new LocationTagged<string>(CurrentToken.Content, CurrentStart));
                        AcceptAndMoveNext();
                        SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                        markupBuilder.Add(OutputAsMarkupLiteral());

                        SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                        AcceptAndMoveNext();
                        SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                        markupBuilder.Add(OutputAsMarkupEphemeralLiteral());

                        var markupBlock = SyntaxFactory.MarkupBlock(markupBuilder.ToList());
                        builder.Add(markupBlock);
                    }
                }
                else
                {
                    Accept(prefixTokens);
                    var valueStart = CurrentStart;
                    PutCurrentBack();

                    var prefix = OutputAsMarkupLiteral();

                    // Dynamic value, start a new block and set the chunk generator
                    using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
                    {
                        var dynamicAttributeValueBuilder = pooledResult.Builder;

                        OtherParserBlock(dynamicAttributeValueBuilder);
                        var value = SyntaxFactory.MarkupDynamicAttributeValue(prefix, SyntaxFactory.GenericBlock(dynamicAttributeValueBuilder.ToList()));
                        builder.Add(value);
                    }
                }
            }
            else
            {
                Accept(prefixTokens);
                var prefix = OutputAsMarkupLiteral();

                // Literal value
                // 'quote' should be "Unknown" if not quoted and tokens coming from the tokenizer should never have
                // "Unknown" type.
                var valueTokens = ReadWhile(token =>
                    // These three conditions find separators which break the attribute value into portions
                    token.Kind != SyntaxKind.Whitespace &&
                    token.Kind != SyntaxKind.NewLine &&
                    token.Kind != SyntaxKind.Transition &&
                    // This condition checks for the end of the attribute value (it repeats some of the checks above
                    // but for now that's ok)
                    !IsEndOfAttributeValue(quote, token));
                Accept(valueTokens);
                var value = OutputAsMarkupLiteral();

                var literalAttributeValue = SyntaxFactory.MarkupLiteralAttributeValue(prefix, value);
                builder.Add(literalAttributeValue);
            }
        }

        private bool IsEndOfAttributeValue(SyntaxKind quote, SyntaxToken token)
        {
            return EndOfFile || token == null ||
                   (quote != SyntaxKind.Marker
                        ? token.Kind == quote // If quoted, just wait for the quote
                        : IsUnquotedEndOfAttributeValue(token));
        }

        private bool IsUnquotedEndOfAttributeValue(SyntaxToken token)
        {
            // If unquoted, we have a larger set of terminating characters:
            // http://dev.w3.org/html5/spec/tokenization.html#attribute-value-unquoted-state
            // Also we need to detect "/" and ">"
            return token.Kind == SyntaxKind.DoubleQuote ||
                   token.Kind == SyntaxKind.SingleQuote ||
                   token.Kind == SyntaxKind.OpenAngle ||
                   token.Kind == SyntaxKind.Equals ||
                   (token.Kind == SyntaxKind.ForwardSlash && NextIs(SyntaxKind.CloseAngle)) ||
                   token.Kind == SyntaxKind.CloseAngle ||
                   token.Kind == SyntaxKind.Whitespace ||
                   token.Kind == SyntaxKind.NewLine;
        }

        private void ParseJavascriptAndEndScriptTag(in SyntaxListBuilder<RazorSyntaxNode> builder, MarkupStartTagSyntax startTag, AcceptedCharactersInternal endTagAcceptedCharacters = AcceptedCharactersInternal.Any)
        {
            var previousNodes = builder.Consume();

            // Special case for <script>: Skip to end of script tag and parse code
            var seenEndScript = false;

            while (!seenEndScript && !EndOfFile)
            {
                ParseMarkupNodes(builder, ParseMode.Text, token => token.Kind == SyntaxKind.OpenAngle);
                var tagStart = CurrentStart;

                if (NextIs(SyntaxKind.ForwardSlash))
                {
                    var openAngle = CurrentToken;
                    NextToken(); // Skip over '<', current is '/'
                    var solidus = CurrentToken;
                    NextToken(); // Skip over '/', current should be text

                    if (At(SyntaxKind.Text) &&
                        string.Equals(CurrentToken.Content, ScriptTagName, StringComparison.OrdinalIgnoreCase))
                    {
                        seenEndScript = true;
                    }

                    // We put everything back because we just wanted to look ahead to see if the current end tag that we're parsing is
                    // the script tag.  If so we'll generate correct code to encompass it.
                    PutCurrentBack(); // Put back whatever was after the solidus
                    PutBack(solidus); // Put back '/'
                    PutBack(openAngle); // Put back '<'

                    // We just looked ahead, this NextToken will set CurrentToken to an open angle bracket.
                    NextToken();
                }

                if (!seenEndScript)
                {
                    AcceptAndMoveNext(); // Accept '<' (not the closing script tag's open angle)
                }
            }

            MarkupEndTagSyntax endTag = null;
            if (seenEndScript)
            {
                var tagStart = CurrentStart;
                builder.Add(OutputAsMarkupLiteral());

                using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
                {
                    var tagBuilder = pooledResult.Builder;
                    SpanContext.EditHandler.AcceptedCharacters = endTagAcceptedCharacters;

                    AcceptAndMoveNext(); // '<'
                    AcceptAndMoveNext(); // '/'
                    ParseMarkupNodes(tagBuilder, ParseMode.Text, token => token.Kind == SyntaxKind.CloseAngle);
                    if (!TryAccept(SyntaxKind.CloseAngle))
                    {
                        Context.ErrorSink.OnError(
                            RazorDiagnosticFactory.CreateParsing_UnfinishedTag(
                                new SourceSpan(SourceLocationTracker.Advance(tagStart, "</"), ScriptTagName.Length),
                                ScriptTagName));
                        var closeAngle = SyntaxFactory.MissingToken(SyntaxKind.CloseAngle);
                        Accept(closeAngle);
                    }
                    tagBuilder.Add(OutputAsMarkupLiteral());
                    endTag = SyntaxFactory.MarkupEndTag(tagBuilder.ToList());
                }
            }

            var element = SyntaxFactory.MarkupElement(startTag, builder.Consume(), endTag);
            builder.AddRange(previousNodes);
            builder.Add(element);
        }

        private bool ParseSpecialTag(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            // Clear the current token builder.
            builder.Add(OutputAsMarkupLiteral());

            return AcceptTokenUntilAll(builder, SyntaxKind.CloseAngle);
        }

        private bool ParseXmlPI(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            Assert(SyntaxKind.OpenAngle);
            AcceptAndMoveNext();
            Assert(SyntaxKind.QuestionMark);
            AcceptAndMoveNext();
            return AcceptTokenUntilAll(builder, SyntaxKind.QuestionMark, SyntaxKind.CloseAngle);
        }

        private bool ParseCData(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            // <![CDATA[...]]>
            Assert(SyntaxKind.OpenAngle);
            AcceptAndMoveNext(); // '<'
            AcceptAndMoveNext(); // '!'
            AcceptAndMoveNext(); // '['
            Debug.Assert(CurrentToken.Kind == SyntaxKind.Text && string.Equals(CurrentToken.Content, "cdata", StringComparison.OrdinalIgnoreCase));
            AcceptAndMoveNext();
            Assert(SyntaxKind.LeftBracket);
            return AcceptTokenUntilAll(builder, SyntaxKind.RightBracket, SyntaxKind.RightBracket, SyntaxKind.CloseAngle);
        }

        private void ParseDoubleTransition(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            AcceptWhile(IsSpacingToken(includeNewLines: true));
            builder.Add(OutputAsMarkupLiteral());

            // First transition
            Assert(SyntaxKind.Transition);
            AcceptAndMoveNext();
            SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
            builder.Add(OutputAsMarkupEphemeralLiteral());

            // Second transition
            AcceptAndMoveNext();
        }

        private void ParseCodeTransition(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            if (Context.NullGenerateWhitespaceAndNewLine)
            {
                // Usually this is set to true when a Code block ends and there is whitespace left after it.
                // We don't want to write it to output.
                Context.NullGenerateWhitespaceAndNewLine = false;
                SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                AcceptWhile(IsSpacingToken(includeNewLines: false));
                if (At(SyntaxKind.NewLine))
                {
                    AcceptAndMoveNext();
                }

                builder.Add(OutputAsMarkupEphemeralLiteral());
            }

            var lastWhitespace = AcceptWhitespaceInLines();
            if (lastWhitespace != null)
            {
                if (Context.DesignTimeMode || !Context.StartOfLine)
                {
                    // Markup owns whitespace in design time mode.
                    Accept(lastWhitespace);
                    lastWhitespace = null;
                }
            }

            PutCurrentBack();
            if (lastWhitespace != null)
            {
                PutBack(lastWhitespace);
            }

            OtherParserBlock(builder);
        }

        private void ParseMarkupComment(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            Assert(SyntaxKind.OpenAngle);

            // Clear the current token builder.
            builder.Add(OutputAsMarkupLiteral());

            using (var pooledResult = Pool.Allocate<RazorSyntaxNode>())
            {
                var htmlCommentBuilder = pooledResult.Builder;

                // Accept the '<', '!' and double-hyphen token at the beginning of the comment block.
                AcceptAndMoveNext();
                AcceptAndMoveNext();
                AcceptAndMoveNext();
                SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                htmlCommentBuilder.Add(OutputAsMarkupLiteral());

                SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.Whitespace;
                while (!EndOfFile)
                {
                    ParseMarkupNodes(htmlCommentBuilder, ParseMode.Text, t => t.Kind == SyntaxKind.DoubleHyphen);
                    var lastDoubleHyphen = AcceptAllButLastDoubleHyphens();

                    if (At(SyntaxKind.CloseAngle))
                    {
                        // Output the content in the comment block as a separate markup
                        SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.Whitespace;
                        htmlCommentBuilder.Add(OutputAsMarkupLiteral());

                        // This is the end of a comment block
                        Accept(lastDoubleHyphen);
                        AcceptAndMoveNext();
                        SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.None;
                        htmlCommentBuilder.Add(OutputAsMarkupLiteral());
                        var commentBlock = SyntaxFactory.MarkupCommentBlock(htmlCommentBuilder.ToList());
                        builder.Add(commentBlock);
                        return;
                    }
                    else if (lastDoubleHyphen != null)
                    {
                        Accept(lastDoubleHyphen);
                    }
                }

                builder.Add(OutputAsMarkupLiteral());
            }
        }

        private void ParseRazorCommentWithLeadingAndTrailingWhitespace(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            if (Context.NullGenerateWhitespaceAndNewLine)
            {
                // Usually this is set to true when a Code block ends and there is whitespace left after it.
                // We don't want to write it to output.
                Context.NullGenerateWhitespaceAndNewLine = false;
                SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                AcceptWhile(IsSpacingToken(includeNewLines: false));
                if (At(SyntaxKind.NewLine))
                {
                    AcceptAndMoveNext();
                }

                builder.Add(OutputAsMarkupEphemeralLiteral());
            }

            var shouldRenderWhitespace = true;
            var lastWhitespace = AcceptWhitespaceInLines();
            var startOfLine = Context.StartOfLine;
            if (lastWhitespace != null)
            {
                // Don't render the whitespace between the start of the line and the razor comment.
                if (startOfLine)
                {
                    AcceptMarkerTokenIfNecessary();
                    // Output the tokens that may have been accepted prior to the whitespace.
                    builder.Add(OutputAsMarkupLiteral());

                    SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                    shouldRenderWhitespace = false;
                }

                Accept(lastWhitespace);
                lastWhitespace = null;
            }

            AcceptMarkerTokenIfNecessary();
            if (shouldRenderWhitespace)
            {
                builder.Add(OutputAsMarkupLiteral());
            }
            else
            {
                builder.Add(OutputAsMarkupEphemeralLiteral());
            }

            var comment = ParseRazorComment();
            builder.Add(comment);

            // Handle the whitespace and newline at the end of a razor comment.
            if (startOfLine &&
                (At(SyntaxKind.NewLine) ||
                (At(SyntaxKind.Whitespace) && NextIs(SyntaxKind.NewLine))))
            {
                AcceptWhile(IsSpacingToken(includeNewLines: false));
                AcceptAndMoveNext();
                SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                builder.Add(OutputAsMarkupEphemeralLiteral());
            }
        }

        private void ParseMisc(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            if (Context.NullGenerateWhitespaceAndNewLine)
            {
                // Usually this is set to true when a Code block ends and there is whitespace left after it.
                // We don't want to write it to output.
                Context.NullGenerateWhitespaceAndNewLine = false;
                SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                AcceptWhile(IsSpacingToken(includeNewLines: false));
                if (At(SyntaxKind.NewLine))
                {
                    AcceptAndMoveNext();
                }

                builder.Add(OutputAsMarkupEphemeralLiteral());
            }

            AcceptWhile(IsSpacingToken(includeNewLines: true));
        }

        private bool ScriptTagExpectsHtml(MarkupStartTagSyntax tagBlock)
        {
            MarkupAttributeBlockSyntax typeAttribute = null;
            for (var i = 0; i < tagBlock.Children.Count; i++)
            {
                var node = tagBlock.Children[i];
                if (node.IsToken || node.IsTrivia)
                {
                    continue;
                }

                if (node is MarkupAttributeBlockSyntax attributeBlock &&
                    attributeBlock.Value.Children.Count > 0 &&
                    IsTypeAttribute(attributeBlock))
                {
                    typeAttribute = attributeBlock;
                    break;
                }
            }

            if (typeAttribute != null)
            {
                var contentValues = typeAttribute.Value.CreateRed().DescendantNodes().Where(n => n.IsToken).Cast<Syntax.SyntaxToken>();

                var scriptType = string.Concat(contentValues.Select(t => t.Content)).Trim();

                // Does not allow charset parameter (or any other parameters).
                return string.Equals(scriptType, "text/html", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static bool IsTypeAttribute(MarkupAttributeBlockSyntax attributeBlock)
        {
            if (attributeBlock.Name.LiteralTokens.Count == 0)
            {
                return false;
            }

            var trimmedStartContent = attributeBlock.Name.ToFullString().TrimStart();
            if (trimmedStartContent.StartsWith("type", StringComparison.OrdinalIgnoreCase) &&
                (trimmedStartContent.Length == 4 ||
                ValidAfterTypeAttributeNameCharacters.Contains(trimmedStartContent[4])))
            {
                return true;
            }

            return false;
        }

        // Internal for testing.
        internal SyntaxToken AcceptAllButLastDoubleHyphens()
        {
            var lastDoubleHyphen = CurrentToken;
            AcceptWhile(s =>
            {
                if (NextIs(SyntaxKind.DoubleHyphen))
                {
                    lastDoubleHyphen = s;
                    return true;
                }

                return false;
            });

            NextToken();

            if (At(SyntaxKind.Text) && IsHyphen(CurrentToken))
            {
                // Doing this here to maintain the order of tokens
                if (!NextIs(SyntaxKind.CloseAngle))
                {
                    Accept(lastDoubleHyphen);
                    lastDoubleHyphen = null;
                }

                AcceptAndMoveNext();
            }

            return lastDoubleHyphen;
        }

        private bool AcceptTokenUntilAll(in SyntaxListBuilder<RazorSyntaxNode> builder, params SyntaxKind[] endSequence)
        {
            while (!EndOfFile)
            {
                ParseMarkupNodes(builder, ParseMode.Text, t => t.Kind == endSequence[0]);
                if (AcceptAll(endSequence))
                {
                    return true;
                }
            }
            Debug.Assert(EndOfFile);
            SpanContext.EditHandler.AcceptedCharacters = AcceptedCharactersInternal.Any;
            return false;
        }

        private ParserState GetParserState(ParseMode mode)
        {
            var whitespace = ReadWhile(IsSpacingToken(includeNewLines: true));
            try
            {
                if (!whitespace.Any() && EndOfFile)
                {
                    return ParserState.EOF;
                }
                else if (At(SyntaxKind.RazorCommentTransition))
                {
                    // Let the comment parser handle the preceding whitespace.
                    return ParserState.RazorComment;
                }
                else if (At(SyntaxKind.Transition))
                {
                    if (NextIs(SyntaxKind.Transition))
                    {
                        return ParserState.DoubleTransition;
                    }

                    // Let the transition parser handle the preceding whitespace.
                    return ParserState.CodeTransition;
                }
                else if (whitespace.Any())
                {
                    // This whitespace isn't sensitive to what comes after it.
                    return ParserState.Misc;
                }
                else if (mode == ParseMode.Text)
                {
                    // We don't want to parse as tags in Text mode. We do this for cases like script tags or <!-- -->.
                    return ParserState.MarkupText;
                }
                else if (At(SyntaxKind.OpenAngle))
                {
                    if (NextIs(SyntaxKind.Bang))
                    {
                        // Checking to see if we meet the conditions of a special '!' tag: <!DOCTYPE, <![CDATA[, <!--.
                        if (!IsBangEscape(lookahead: 1))
                        {
                            if (IsHtmlCommentAhead())
                            {
                                return ParserState.MarkupComment;
                            }
                            else if (Lookahead(2)?.Kind == SyntaxKind.LeftBracket &&
                                Lookahead(3) is SyntaxToken tagName &&
                                string.Equals(tagName.Content, "cdata", StringComparison.OrdinalIgnoreCase) &&
                                Lookahead(4)?.Kind == SyntaxKind.LeftBracket)
                            {
                                return ParserState.CData;
                            }
                            else
                            {
                                // E.g. <!DOCTYPE ...
                                return ParserState.SpecialTag;
                            }
                        }
                    }
                    else if (NextIs(SyntaxKind.QuestionMark))
                    {
                        return ParserState.XmlPI;
                    }

                    // Regular tag
                    return ParserState.Tag;
                }
                else
                {
                    return ParserState.Unknown;
                }
            }
            finally
            {
                if (whitespace.Any())
                {
                    PutCurrentBack();
                    PutBack(whitespace);
                    EnsureCurrent();
                }
            }
        }

        private bool TryParseBangEscape(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            if (IsBangEscape(lookahead: 0))
            {
                builder.Add(OutputAsMarkupLiteral());

                // Accept the parser escape character '!'.
                Assert(SyntaxKind.Bang);
                AcceptAndMoveNext();

                // Setup the metacode span that we will be outputing.
                SpanContext.ChunkGenerator = SpanChunkGenerator.Null;
                builder.Add(OutputAsMetaCode(Output()));
                return true;
            }

            return false;
        }

        private bool IsBangEscape(int lookahead)
        {
            var potentialBang = Lookahead(lookahead);

            if (potentialBang != null &&
                potentialBang.Kind == SyntaxKind.Bang)
            {
                var afterBang = Lookahead(lookahead + 1);

                return afterBang != null &&
                    afterBang.Kind == SyntaxKind.Text &&
                    !string.Equals(afterBang.Content, "DOCTYPE", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private bool IsHtmlCommentAhead()
        {
            // From HTML5 Specification, available at http://www.w3.org/TR/html52/syntax.html#comments

            // Comments must have the following format:
            // 1. The string "<!--"
            // 2. Optionally, text, with the additional restriction that the text
            //      2.1 must not start with the string ">" nor start with the string "->"
            //      2.2 nor contain the strings
            //          2.2.1 "<!--"
            //          2.2.2 "-->" As we will be treating this as a comment ending, there is no need to handle this case at all.
            //          2.2.3 "--!>"
            //      2.3 nor end with the string "<!-".
            // 3. The string "-->"

            if (!(At(SyntaxKind.OpenAngle) && NextIs(SyntaxKind.Bang)))
            {
                return false;
            }

            // Consume '<' and '!'
            var openAngle = EatCurrentToken();
            var bangToken = EatCurrentToken();

            try
            {
                if (EndOfFile || CurrentToken.Kind != SyntaxKind.DoubleHyphen)
                {
                    return false;
                }

                // Check condition 2.1
                if (NextIs(SyntaxKind.CloseAngle) || NextIs(next => IsHyphen(next) && NextIs(SyntaxKind.CloseAngle)))
                {
                    return false;
                }

                // Check condition 2.2
                var isValidComment = false;
                LookaheadUntil((token, prevTokens) =>
                {
                    if (token.Kind == SyntaxKind.DoubleHyphen)
                    {
                        if (NextIs(SyntaxKind.CloseAngle))
                        {
                            // Check condition 2.3: We're at the end of a comment. Check to make sure the text ending is allowed.
                            isValidComment = !IsCommentContentEndingInvalid(prevTokens);
                            return true;
                        }
                        else if (NextIs(ns => IsHyphen(ns) && NextIs(SyntaxKind.CloseAngle)))
                        {
                            // Check condition 2.3: we're at the end of a comment, which has an extra dash.
                            // Need to treat the dash as part of the content and check the ending.
                            // However, that case would have already been checked as part of check from 2.2.1 which
                            // would already fail this iteration and we wouldn't get here
                            isValidComment = true;
                            return true;
                        }
                        else if (NextIs(ns => ns.Kind == SyntaxKind.Bang && NextIs(SyntaxKind.CloseAngle)))
                        {
                            // This is condition 2.2.3
                            isValidComment = false;
                            return true;
                        }
                    }
                    else if (token.Kind == SyntaxKind.OpenAngle)
                    {
                        // Checking condition 2.2.1
                        if (NextIs(ns => ns.Kind == SyntaxKind.Bang && NextIs(SyntaxKind.DoubleHyphen)))
                        {
                            isValidComment = false;
                            return true;
                        }
                    }

                    return false;
                });

                return isValidComment;
            }
            finally
            {
                // Put back the consumed tokens for later parsing.
                PutCurrentBack();
                PutBack(bangToken);
                PutBack(openAngle);
                EnsureCurrent();
            }
        }

        private bool IsConditionalAttributeName(string name)
        {
            var attributeCanBeConditional =
                Context.FeatureFlags.EXPERIMENTAL_AllowConditionalDataDashAttributes ||
                !name.StartsWith("data-", StringComparison.OrdinalIgnoreCase);
            return attributeCanBeConditional;
        }

        private void OtherParserBlock(in SyntaxListBuilder<RazorSyntaxNode> builder)
        {
            AcceptMarkerTokenIfNecessary();
            builder.Add(OutputAsMarkupLiteral());

            RazorSyntaxNode codeBlock;
            using (PushSpanContextConfig())
            {
                codeBlock = CodeParser.ParseBlock();
            }

            builder.Add(codeBlock);
            InitializeContext(SpanContext);
            NextToken();
        }

        /// <summary>
        /// Verifies, that the sequence doesn't end with the "&lt;!-" HtmlTokens. Note, the first token is an opening bracket token
        /// </summary>
        internal static bool IsCommentContentEndingInvalid(IEnumerable<SyntaxToken> sequence)
        {
            var reversedSequence = sequence.Reverse();
            var index = 0;
            foreach (var item in reversedSequence)
            {
                if (!item.IsEquivalentTo(nonAllowedHtmlCommentEnding[index++]))
                {
                    return false;
                }

                if (index == nonAllowedHtmlCommentEnding.Length)
                {
                    return true;
                }
            }

            return false;
        }

        protected static Func<SyntaxToken, bool> IsSpacingToken(bool includeNewLines)
        {
            return token => token.Kind == SyntaxKind.Whitespace || (includeNewLines && token.Kind == SyntaxKind.NewLine);
        }

        internal static bool IsHyphen(SyntaxToken token)
        {
            return token.Kind == SyntaxKind.Text && token.Content == "-";
        }

        internal static bool IsValidAttributeNameToken(SyntaxToken token)
        {
            if (token == null)
            {
                return false;
            }

            // These restrictions cover most of the spec defined: http://www.w3.org/TR/html5/syntax.html#attributes-0
            // However, it's not all of it. For instance we don't special case control characters or allow OpenAngle.
            // It also doesn't try to exclude Razor specific features such as the @ transition. This is based on the
            // expectation that the parser handles such scenarios prior to falling through to name resolution.
            var tokenType = token.Kind;
            return tokenType != SyntaxKind.Whitespace &&
                tokenType != SyntaxKind.NewLine &&
                tokenType != SyntaxKind.CloseAngle &&
                tokenType != SyntaxKind.OpenAngle &&
                tokenType != SyntaxKind.ForwardSlash &&
                tokenType != SyntaxKind.DoubleQuote &&
                tokenType != SyntaxKind.SingleQuote &&
                tokenType != SyntaxKind.Equals &&
                tokenType != SyntaxKind.Marker;
        }

        private static bool IsTagRecoveryStopPoint(SyntaxToken token)
        {
            return token.Kind == SyntaxKind.CloseAngle ||
                   token.Kind == SyntaxKind.ForwardSlash ||
                   token.Kind == SyntaxKind.OpenAngle ||
                   token.Kind == SyntaxKind.SingleQuote ||
                   token.Kind == SyntaxKind.DoubleQuote;
        }

        private void DefaultMarkupSpanContext(SpanContextBuilder spanContext)
        {
            spanContext.ChunkGenerator = new MarkupChunkGenerator();
            spanContext.EditHandler = new SpanEditHandler(Language.TokenizeString, AcceptedCharactersInternal.Any);
        }

        private enum ParseMode
        {
            Markup,
            MarkupInCodeBlock,
            Text,
        }

        private enum MarkupTagMode
        {
            Normal,
            Void,
            SelfClosing,
            Script,
            Invalid,
        }

        private class TagTracker
        {
            public TagTracker(
                string tagName,
                MarkupStartTagSyntax startTag,
                SourceLocation tagLocation,
                SyntaxList<RazorSyntaxNode> previousNodes)
            {
                TagName = tagName;
                StartTag = startTag;
                TagLocation = tagLocation;
                PreviousNodes = previousNodes;
            }

            public string TagName { get; }

            public MarkupStartTagSyntax StartTag { get; }

            public SourceLocation TagLocation { get; }

            public SyntaxList<RazorSyntaxNode> PreviousNodes { get; }
        }
    }
}

using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;
using System.Web;

#pragma warning disable BL0006 // Do not use RenderTree types

namespace MarkdownBlazor
{
    public class Markdown : ComponentBase
    {
        [Parameter]
        public RenderFragment? ChildContent { get; set; }

        [Parameter]
        public bool AddRenderTime { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(2, "div");
            builder.AddAttribute(3, "class", "markdown");

            const int baseSequence = 3;

            Stopwatch? stopwatch = null;

            if (AddRenderTime)
            {
                stopwatch = new();
                stopwatch.Start();
            }

            if (ChildContent is not null)
            {
                var nestedBuilder = new RenderTreeBuilder();

                ChildContent(nestedBuilder);

                var frames = nestedBuilder.GetFrames();

                var pipeline = new MarkdownPipelineBuilder()
                    .UseCitations()
                    .UseGridTables()
                    .UsePipeTables()                    
                    .Build();

                var openBlockBuilder = new StringBuilder();

                var renderTextBuilder = new StringBuilder();
                var renderWriter = new StringWriter(renderTextBuilder);
                var htmlRenderer = new HtmlRenderer(renderWriter);

                pipeline.Setup(htmlRenderer);
                                
                var initiatingOpenBlockSequence = 0;
                var lastBlockWasOpenBlock = false;
                var sequenceOffset = baseSequence;
                var minIndent = 0;

                for (var frameIdx = 0; frameIdx < frames.Count; frameIdx++)
                {
                    RenderFrame(
                        builder,
                        ref sequenceOffset, 
                        frames, 
                        pipeline, 
                        openBlockBuilder, 
                        renderTextBuilder,
                        htmlRenderer,
                        false, 
                        ref initiatingOpenBlockSequence, 
                        ref minIndent,
                        ref lastBlockWasOpenBlock, 
                        ref frameIdx);
                }

                if (lastBlockWasOpenBlock)
                {
                    var document = Markdig.Markdown.Parse(openBlockBuilder.ToString(), pipeline);

                    RenderDocument(initiatingOpenBlockSequence, ref sequenceOffset, builder, renderTextBuilder, htmlRenderer, document);

                    openBlockBuilder.Clear();
                }
            }

            builder.CloseElement();

            if (stopwatch is not null)
            {
                stopwatch.Stop();

                builder.AddMarkupContent(99999, $"<code>rendered in: {stopwatch.Elapsed.TotalMilliseconds:0.00}ms</code>");
            }
        }

        private static void RenderFrame(
            RenderTreeBuilder builder, 
            ref int sequenceOffset,
            ArrayRange<RenderTreeFrame> frames,
            MarkdownPipeline pipeline, 
            StringBuilder openBlockBuilder,
            StringBuilder renderTextBuilder,
            HtmlRenderer renderer, 
            bool forceDoNotCloseBlock,
            ref int initiatingOpenBlockSequence, 
            ref int existingMinimumIndent,
            ref bool lastBlockWasOpenBlock, ref int frameIdx)
        {
            var frame = frames.Array[frameIdx];

            if (frame.FrameType == RenderTreeFrameType.Markup)
            {
                var thisFrameContent = RemoveIndentationFromMultilineString(frame.MarkupContent, ref existingMinimumIndent);

                var parseContent = thisFrameContent;

                if (lastBlockWasOpenBlock)
                {
                    // No content on this line, and previous block was open; most likely extraneous content.
                    openBlockBuilder.Append(thisFrameContent);
                    parseContent = openBlockBuilder.ToString();
                }

                var document = Markdig.Markdown.Parse(parseContent, pipeline);

                if (document.LastChild?.IsOpen ?? false)
                {
                    // Last parsed block is open, we might be in the middle of a table.                            
                    if (!lastBlockWasOpenBlock)
                    {
                        initiatingOpenBlockSequence = frame.Sequence;
                        lastBlockWasOpenBlock = true;
                        openBlockBuilder.Append(thisFrameContent);
                    }

                    // Remove extraneous whitespace from the end of the line.
                    var lastIndexOfContent = thisFrameContent.AsSpan().LastIndexOfAnyExcept("\t\n\r");

                    var startOfFrameContent = openBlockBuilder.Length - thisFrameContent.Length;
                                        
                    openBlockBuilder.Remove(startOfFrameContent + lastIndexOfContent + 1, thisFrameContent.Length - lastIndexOfContent - 1);
                }
                else
                {
                    // Last block was closed; render to html and reset open block builder.
                    if (lastBlockWasOpenBlock && !forceDoNotCloseBlock)
                    {
                        RenderDocument(initiatingOpenBlockSequence, ref sequenceOffset, builder, renderTextBuilder, renderer, document);

                        openBlockBuilder.Clear();
                        lastBlockWasOpenBlock = false;
                    }
                    else
                    {
                        RenderDocument(frame.Sequence, ref sequenceOffset, builder, renderTextBuilder, renderer, document);
                    }
                }
            }
            else if (frame.FrameType == RenderTreeFrameType.Text)
            {
                if (lastBlockWasOpenBlock)
                {
                    // Escape html string.
                    openBlockBuilder.Append(HttpUtility.HtmlEncode(frame.TextContent));
                }
                else
                {
                    builder.AddContent(frame.Sequence + sequenceOffset, frame.TextContent);
                }
            }
            else if (frame.FrameType == RenderTreeFrameType.Element)
            {
                bool isActualElement = false;

                if (!string.IsNullOrWhiteSpace(frame.ElementName))
                {
                    isActualElement = true;

                    if (lastBlockWasOpenBlock)
                    {
                        openBlockBuilder.Append($"<{frame.ElementName}");
                    }
                    else
                    {
                        // An element; if we're in an open block, render the markup to text; else just add the frames.
                        builder.OpenElement(frame.Sequence + sequenceOffset, frame.ElementName);
                    }
                }

                int frameLength = frame.ElementSubtreeLength;
                bool seenAllAttributes = false;

                while (frameLength > 1)
                {
                    frameLength--;

                    // Look at the next frame.
                    frameIdx++;

                    var nextFrame = frames.Array[frameIdx];

                    if (nextFrame.FrameType != RenderTreeFrameType.Attribute && lastBlockWasOpenBlock && !seenAllAttributes && isActualElement)
                    {
                        // Last attribute.
                        seenAllAttributes = true;
                        openBlockBuilder.Append(">");
                    }

                    RenderFrame(
                        builder, 
                        ref sequenceOffset, 
                        frames,
                        pipeline, 
                        openBlockBuilder,
                        renderTextBuilder,
                        renderer,
                        forceDoNotCloseBlock,
                        ref initiatingOpenBlockSequence,
                        ref existingMinimumIndent,
                        ref lastBlockWasOpenBlock, 
                        ref frameIdx);
                }

                if (isActualElement)
                {
                    if (lastBlockWasOpenBlock)
                    {
                        if (!seenAllAttributes)
                        {
                            openBlockBuilder.Append(" />");
                        }
                        else
                        {
                            openBlockBuilder.Append($"</{frame.ElementName}>");
                        }
                    }
                    else
                    {
                        builder.CloseElement();
                    }
                }
            }
            else if (frame.FrameType == RenderTreeFrameType.Attribute)
            {
                if (lastBlockWasOpenBlock)
                {
                    openBlockBuilder.Append($" {frame.AttributeName}=\"{HttpUtility.HtmlEncode(frame.AttributeValue.ToString())}\"");
                }
                else
                {
                    builder.AddAttribute(frame.Sequence + sequenceOffset, frame);
                }
            }
        }

        static void RenderDocument(int baseSequence, ref int sequenceOffset, RenderTreeBuilder treeBuilder, StringBuilder htmlRenderBuilder, HtmlRenderer renderer, MarkdownDocument document)
        {
            foreach (var block in document)
            {
                renderer.Render(block);
                sequenceOffset++;
                treeBuilder.AddMarkupContent(baseSequence + sequenceOffset, htmlRenderBuilder.ToString());
                htmlRenderBuilder.Clear();
            }
        }

        static string RemoveIndentationFromMultilineString(string input, ref int existingMin)
        {
            var lines = input.AsSpan().EnumerateLines();

            var minIndentation = existingMin;
            int lineCount = 0;

            foreach (var line in lines)
            {
                lineCount++;

                if (line.IsWhiteSpace())
                {
                    continue;
                }

                var thisIndent = line.IndexOfAnyExcept(" \t");

                if (minIndentation == 0)
                {
                    minIndentation = thisIndent;
                }
            }

            if (existingMin == 0)
            {
                existingMin = minIndentation;
            }

            var sb = new StringBuilder();
            var currentLine = 0;

            foreach (var line in lines)
            {
                currentLine++;

                if (line.IsWhiteSpace())
                {
                    sb.AppendLine();
                }
                else
                {
                    // Find first index of the non-whitespace characters.
                    var removePoint = line.IndexOfAnyExcept(" \t");

                    if (removePoint > minIndentation)
                    {
                        removePoint -= minIndentation;
                    }

                    sb.Append(line.Slice(removePoint));

                    if (currentLine < lineCount)
                    {
                        sb.Append('\n');
                    }
                }
            }

            return sb.ToString();
        }
    }
}

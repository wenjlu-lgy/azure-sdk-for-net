﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SnippetGenerator
{
    public class DirectoryProcessor
    {
        private const string _snippetPrefix = "Snippet:";
        private readonly string _directory;
        private readonly Lazy<List<Snippet>> _snippets;

        private UTF8Encoding _utf8EncodingWithoutBOM;

        public DirectoryProcessor(string directory)
        {
            _directory = directory;
            _snippets = new Lazy<List<Snippet>>(DiscoverSnippets);
        }

        public void Process()
        {
            foreach (var mdFile in Directory.EnumerateFiles(_directory, "*.md", SearchOption.AllDirectories))
            {
                Console.WriteLine($"Processing {mdFile}");

                var text = File.ReadAllText(mdFile);
                bool changed = false;

                text = MarkdownProcessor.Process(text, s =>
                {
                    var selectedSnippets = _snippets.Value.Where(snip => snip.Name == s).ToArray();
                    if (selectedSnippets.Length > 1)
                    {
                        throw new InvalidOperationException($"Multiple snippets with the name '{s}' defined '{_directory}'");
                    }
                    if (selectedSnippets.Length == 0)
                    {
                        throw new InvalidOperationException($"Snippet '{s}' not found in directory '{_directory}'");
                    }

                    var selectedSnippet = selectedSnippets.Single();
                    Console.WriteLine($"Replaced {selectedSnippet.Name}");
                    changed = true;
                    return FormatSnippet(selectedSnippet.Text);
                });

                if (changed)
                {
                    _utf8EncodingWithoutBOM = new UTF8Encoding(false);
                    File.WriteAllText(mdFile, text, _utf8EncodingWithoutBOM);
                }
            }
        }

        private List<Snippet> DiscoverSnippets()
        {
            var snippets = GetSnippetsInDirectory(_directory);
            Console.WriteLine($"Discovered snippets:");

            foreach (var snippet in snippets)
            {
                Console.WriteLine($" {snippet.Name} in {snippet.FilePath}");
            }

            return snippets;
        }

        private string FormatSnippet(SourceText text)
        {
            int minIndent = int.MaxValue;
            int firstLine = 0;
            var lines = text.Lines.Select(l => l.ToString()).ToArray();

            int lastLine = lines.Length - 1;

            while (firstLine < lines.Length && string.IsNullOrWhiteSpace(lines[firstLine]))
            {
                firstLine++;
            }

            while (lastLine > 0 && string.IsNullOrWhiteSpace(lines[lastLine]))
            {
                lastLine--;
            }

            for (var index = firstLine; index <= lastLine; index++)
            {
                var textLine = lines[index];

                if (string.IsNullOrWhiteSpace(textLine))
                {
                    continue;
                }

                int i;
                for (i = 0; i < textLine.Length; i++)
                {
                    if (!char.IsWhiteSpace(textLine[i])) break;
                }

                minIndent = Math.Min(minIndent, i);
            }

            var stringBuilder = new StringBuilder();
            for (var index = firstLine; index <= lastLine; index++)
            {
                var line = lines[index];
                line = string.IsNullOrWhiteSpace(line) ? string.Empty : line.Substring(minIndent);
                stringBuilder.AppendLine(line);
            }

            return stringBuilder.ToString();
        }

        private List<Snippet> GetSnippetsInDirectory(string baseDirectory)
        {
            var list = new List<Snippet>();
            foreach (var file in Directory.GetFiles(baseDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(
                    File.ReadAllText(file),
                    new CSharpParseOptions(LanguageVersion.Preview),
                    path: file);
                list.AddRange(GetAllSnippets(syntaxTree));
            }

            return list;
        }

        private Snippet[] GetAllSnippets(SyntaxTree syntaxTree)
        {
            var snippets = new List<Snippet>();
            var directiveWalker = new DirectiveWalker();
            directiveWalker.Visit(syntaxTree.GetRoot());

            foreach (var region in directiveWalker.Regions)
            {
                var syntaxTrivia = region.Item1.EndOfDirectiveToken.LeadingTrivia.First(t => t.IsKind(SyntaxKind.PreprocessingMessageTrivia));
                var fromBounds = TextSpan.FromBounds(
                    region.Item1.GetLocation().SourceSpan.End,
                    region.Item2.GetLocation().SourceSpan.Start);

                var regionName = syntaxTrivia.ToString();
                if (regionName.StartsWith(_snippetPrefix))
                {
                    snippets.Add(new Snippet(syntaxTrivia.ToString(), syntaxTree.GetText().GetSubText(fromBounds), syntaxTree.FilePath));
                }
            }

            return snippets.ToArray();
        }

        class DirectiveWalker : CSharpSyntaxWalker
        {
            private Stack<RegionDirectiveTriviaSyntax> _regions = new Stack<RegionDirectiveTriviaSyntax>();
            public List<(RegionDirectiveTriviaSyntax, EndRegionDirectiveTriviaSyntax)> Regions { get; } = new List<(RegionDirectiveTriviaSyntax, EndRegionDirectiveTriviaSyntax)>();

            public DirectiveWalker() : base(SyntaxWalkerDepth.StructuredTrivia)
            {
            }

            public override void VisitRegionDirectiveTrivia(RegionDirectiveTriviaSyntax node)
            {
                base.VisitRegionDirectiveTrivia(node);
                _regions.Push(node);
            }

            public override void VisitEndRegionDirectiveTrivia(EndRegionDirectiveTriviaSyntax node)
            {
                base.VisitEndRegionDirectiveTrivia(node);
                Regions.Add((_regions.Pop(), node));
            }
        }
    }
}
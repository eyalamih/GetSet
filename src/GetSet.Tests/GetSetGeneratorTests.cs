using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace GetSet.Tests
{
    public class GetSetGeneratorTests
    {
        // ------------------------------------------------------------------ //
        //  Helpers                                                             //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Runs the GetSet generator against <paramref name="source"/> and
        /// returns all generated source files as a dictionary of
        /// hintName → generated source.
        /// </summary>
        private static (Compilation output, IList<Diagnostic> diagnostics, Dictionary<string, string> generated)
            RunGenerator(string source)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source);

            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .Cast<MetadataReference>()
                .ToList();

            var compilation = CSharpCompilation.Create(
                "TestAssembly",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var generator = new GetSetGenerator();
            GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

            var result = driver.GetRunResult();
            var generated = result.GeneratedTrees
                .ToDictionary(
                    t => System.IO.Path.GetFileName(t.FilePath),
                    t => t.GetText().ToString());

            return (outputCompilation, diagnostics.ToList(), generated);
        }

        // ------------------------------------------------------------------ //
        //  Tests                                                               //
        // ------------------------------------------------------------------ //

        [Fact]
        public void SimpleGetterAndSetter_EmitsBackingFieldAndProperty()
        {
            string source = @"
namespace MyApp
{
    public partial class Counter
    {
        public int num = 5
        {
            get { return num; }
            set { if (value >= 0) num = value; }
        }
    }
}";
            var (_, diagnostics, generated) = RunGenerator(source);

            // No generator errors.
            Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

            // There should be at least one generated file for the class.
            var classFile = generated.Values.FirstOrDefault(v => v.Contains("partial class Counter"));
            Assert.NotNull(classFile);

            // Backing field emitted.
            Assert.Contains("private int _num", classFile);

            // Initializer preserved.
            Assert.Contains("= 5", classFile);

            // Property with getter.
            Assert.Contains("public int num", classFile);
            Assert.Contains("get", classFile);

            // Setter emitted.
            Assert.Contains("set", classFile);
        }

        [Fact]
        public void GetterOnly_EmitsNoSetter()
        {
            string source = @"
namespace MyApp
{
    public partial class ReadOnlyHolder
    {
        public string name = ""Alice""
        {
            get { return name; }
        }
    }
}";
            var (_, diagnostics, generated) = RunGenerator(source);
            Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

            var classFile = generated.Values.FirstOrDefault(v => v.Contains("partial class ReadOnlyHolder"));
            Assert.NotNull(classFile);

            Assert.Contains("private string _name", classFile);
            Assert.Contains("get", classFile);
            // No setter block.
            Assert.DoesNotContain("set", classFile!.Split(new[]{"get"}, StringSplitOptions.None).Last());
        }

        [Fact]
        public void BackingFieldRefs_RewrittenInsideAccessors()
        {
            string source = @"
namespace MyApp
{
    public partial class Validator
    {
        public int count = 0
        {
            get { return count; }
            set { if (value > 0) count = value; }
        }
    }
}";
            var (_, _, generated) = RunGenerator(source);

            var classFile = generated.Values.FirstOrDefault(v => v.Contains("partial class Validator"));
            Assert.NotNull(classFile);

            // The word "count" in accessor bodies should have been rewritten.
            // Check backing field name appears.
            Assert.Contains("_count", classFile);
        }

        [Fact]
        public void NoShorthand_NoClassGenerated()
        {
            string source = @"
namespace MyApp
{
    public partial class PlainClass
    {
        private int _x = 0;
        public int X { get { return _x; } set { _x = value; } }
    }
}";
            var (_, diagnostics, generated) = RunGenerator(source);
            Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

            // Only the injected attribute file should be present.
            Assert.All(generated.Values, v => Assert.DoesNotContain("partial class PlainClass", v));
        }

        [Fact]
        public void MultipleShorthandFields_AllEmitted()
        {
            string source = @"
namespace MyApp
{
    public partial class MultiField
    {
        public int width = 100
        {
            get { return width; }
            set { if (value > 0) width = value; }
        }

        public int height = 200
        {
            get { return height; }
            set { if (value > 0) height = value; }
        }
    }
}";
            var (_, _, generated) = RunGenerator(source);
            var classFile = generated.Values.FirstOrDefault(v => v.Contains("partial class MultiField"));
            Assert.NotNull(classFile);

            Assert.Contains("_width", classFile);
            Assert.Contains("_height", classFile);
            Assert.Contains("public int width", classFile);
            Assert.Contains("public int height", classFile);
        }

        [Fact]
        public void AttributeIsInjected()
        {
            var (_, _, generated) = RunGenerator("class Dummy {}");
            Assert.True(generated.ContainsKey("GetSetAttribute.g.cs"));
            Assert.Contains("UseGetSetAttribute", generated["GetSetAttribute.g.cs"]);
        }
    }
}

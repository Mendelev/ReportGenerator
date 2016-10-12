using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Palmmedia.ReportGenerator.Logging;
using Palmmedia.ReportGenerator.Parser.Analysis;
using Palmmedia.ReportGenerator.Properties;

namespace Palmmedia.ReportGenerator.Parser
{
    /// <summary>
    /// Parser for XML reports generated by dotCover.
    /// </summary>
    internal class DotCoverParser : ParserBase
    {
        /// <summary>
        /// Regex to analyze if a method name belongs to a lamda expression.
        /// </summary>
        private const string LambdaMethodRegex = @"<.+>.+__.+\(.*\)";

        /// <summary>
        /// The Logger.
        /// </summary>
        private static readonly ILogger Logger = LoggerFactory.GetLogger(typeof(DotCoverParser));

        /// <summary>
        /// The module elements of the report.
        /// </summary>
        private XElement[] modules;

        /// <summary>
        /// The file elements of the report.
        /// </summary>
        private XElement[] files;

        /// <summary>
        /// Initializes a new instance of the <see cref="DotCoverParser"/> class.
        /// </summary>
        /// <param name="report">The report file as XContainer.</param>
        internal DotCoverParser(XContainer report)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            this.modules = report.Descendants("Assembly")
                .ToArray();
            this.files = report.Descendants("File").ToArray();

            var assemblyNames = this.modules
                .Select(m => m.Attribute("Name").Value)
                .Distinct()
                .OrderBy(a => a)
                .ToArray();

            Parallel.ForEach(assemblyNames, assemblyName => this.AddAssembly(this.ProcessAssembly(assemblyName)));

            this.modules = null;
            this.files = null;
        }

        /// <summary>
        /// Extracts the methods/properties of the given <see cref="XElement">XElements</see>.
        /// </summary>
        /// <param name="codeFile">The code file.</param>
        /// <param name="fileId">The id of the file.</param>
        /// <param name="methods">The methods.</param>
        private static void SetCodeElements(CodeFile codeFile, string fileId, IEnumerable<XElement> methods)
        {
            foreach (var method in methods)
            {
                string methodName = ExtractMethodName(method.Parent.Attribute("Name").Value, method.Attribute("Name").Value);

                if (Regex.IsMatch(methodName, LambdaMethodRegex))
                {
                    continue;
                }

                CodeElementType type = CodeElementType.Method;

                if (methodName.StartsWith("get_")
                    || methodName.StartsWith("set_"))
                {
                    type = CodeElementType.Property;
                    methodName = methodName.Substring(4);
                }

                var statement = method
                    .Elements("Statement")
                    .FirstOrDefault();

                if (statement != null && statement.Attribute("FileIndex").Value == fileId)
                {
                    int line = int.Parse(statement.Attribute("Line").Value, CultureInfo.InvariantCulture);
                    codeFile.AddCodeElement(new CodeElement(methodName, type, line));
                }
            }
        }

        /// <summary>
        /// Extracts the method name. For async methods the original name is returned.
        /// </summary>
        /// <param name="typeName">The name of the class.</param>
        /// <param name="methodName">The full method name.</param>
        /// <returns>The method name.</returns>
        private static string ExtractMethodName(string typeName, string methodName)
        {
            Match match = Regex.Match(typeName + methodName, @"<(?<CompilerGeneratedName>.+)>.+__.+MoveNext\(\):.+$");

            if (match.Success)
            {
                return match.Groups["CompilerGeneratedName"].Value + "()";
            }

            return methodName.Substring(0, methodName.LastIndexOf(':'));
        }

        /// <summary>
        /// Processes the given assembly.
        /// </summary>
        /// <param name="assemblyName">Name of the assembly.</param>
        /// <returns>The <see cref="Assembly"/>.</returns>
        private Assembly ProcessAssembly(string assemblyName)
        {
            Logger.DebugFormat("  " + Resources.CurrentAssembly, assemblyName);

            var assemblyElement = this.modules
                .Where(m => m.Attribute("Name").Value.Equals(assemblyName));

            var classNames = assemblyElement
                .Elements("Namespace")
                .Elements("Type")
                .Concat(assemblyElement.Elements("Type"))
                .Where(c => !c.Attribute("Name").Value.Contains("__"))
                .Select(c => c.Parent.Attribute("Name").Value + "." + c.Attribute("Name").Value)
                .Distinct()
                .OrderBy(name => name)
                .ToArray();

            var assembly = new Assembly(assemblyName);

            Parallel.ForEach(classNames, className => assembly.AddClass(this.ProcessClass(assembly, className)));

            return assembly;
        }

        /// <summary>
        /// Processes the given class.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        /// <param name="className">Name of the class.</param>
        /// <returns>The <see cref="Class"/>.</returns>
        private Class ProcessClass(Assembly assembly, string className)
        {
            var assemblyElement = this.modules
                .Where(m => m.Attribute("Name").Value.Equals(assembly.Name));

            var filesIdsOfClass = assemblyElement
                .Elements("Namespace")
                .Elements("Type")
                .Concat(assemblyElement.Elements("Type"))
                .Where(c => (c.Parent.Attribute("Name").Value + "." + c.Attribute("Name").Value).Equals(className))
                .Descendants("Statement")
                .Select(c => c.Attribute("FileIndex").Value)
                .Distinct()
                .ToArray();

            var @class = new Class(className, assembly);

            foreach (var fileId in filesIdsOfClass)
            {
                @class.AddFile(this.ProcessFile(fileId, @class));
            }

            return @class;
        }

        /// <summary>
        /// Processes the file.
        /// </summary>
        /// <param name="fileId">The id of the file.</param>
        /// <param name="class">The class.</param>
        /// <returns>The <see cref="CodeFile"/>.</returns>
        private CodeFile ProcessFile(string fileId, Class @class)
        {
            var assemblyElement = this.modules
                .Where(m => m.Attribute("Name").Value.Equals(@class.Assembly.Name));

            var methodsOfFile = assemblyElement
               .Elements("Namespace")
               .Elements("Type")
               .Concat(assemblyElement.Elements("Type"))
               .Where(c => (c.Parent.Attribute("Name").Value + "." + c.Attribute("Name").Value).Equals(@class.Name))
               .Descendants("Method")
               .ToArray();

            var statements = methodsOfFile
               .Elements("Statement")
               .Where(c => c.Attribute("FileIndex").Value == fileId)
               .Select(c => new
               {
                   LineNumberStart = int.Parse(c.Attribute("Line").Value, CultureInfo.InvariantCulture),
                   LineNumberEnd = int.Parse(c.Attribute("EndLine").Value, CultureInfo.InvariantCulture),
                   Visited = c.Attribute("Covered").Value == "True"
               })
               .OrderBy(seqpnt => seqpnt.LineNumberEnd)
               .ToArray();

            int[] coverage = new int[] { };
            LineVisitStatus[] lineVisitStatus = new LineVisitStatus[] { };

            if (statements.Length > 0)
            {
                coverage = new int[statements[statements.LongLength - 1].LineNumberEnd + 1];
                lineVisitStatus = new LineVisitStatus[statements[statements.LongLength - 1].LineNumberEnd + 1];

                for (int i = 0; i < coverage.Length; i++)
                {
                    coverage[i] = -1;
                }

                foreach (var statement in statements)
                {
                    for (int lineNumber = statement.LineNumberStart; lineNumber <= statement.LineNumberEnd; lineNumber++)
                    {
                        int visits = statement.Visited ? 1 : 0;
                        coverage[lineNumber] = coverage[lineNumber] == -1 ? visits : Math.Min(coverage[lineNumber] + visits, 1);
                        lineVisitStatus[lineNumber] = lineVisitStatus[lineNumber] == LineVisitStatus.Covered || statement.Visited ? LineVisitStatus.Covered : LineVisitStatus.NotCovered;
                    }
                }
            }

            string filePath = this.files.First(f => f.Attribute("Index").Value == fileId).Attribute("Name").Value;
            var codeFile = new CodeFile(filePath, coverage, lineVisitStatus);

            SetCodeElements(codeFile, fileId, methodsOfFile);

            return codeFile;
        }
    }
}

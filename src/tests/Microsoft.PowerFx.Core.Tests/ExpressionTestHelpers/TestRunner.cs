﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.PowerFx.Core.Public.Types;
using Microsoft.PowerFx.Core.Public.Values;

namespace Microsoft.PowerFx.Core.Tests
{    
    /// <summary>
    /// Parse test files and invoke runners to execute them. 
    /// </summary>
    public class TestRunner
    {
        private readonly BaseRunner[] _runners;
        private readonly List<TestCase> _tests = new List<TestCase>();

        // Mapping of a Test's key to the test case in _test list.
        // Used for when we need to update the test. 
        private readonly Dictionary<string, TestCase> _keyToTests = new Dictionary<string, TestCase>(StringComparer.Ordinal);

        public IEnumerable<TestCase> Tests => _tests;

        // Files that have been disabled. 
        public HashSet<string> DisabledFiles = new HashSet<string>();

        public TestRunner(params BaseRunner[] runners)
        {
            _runners = runners;
        }

        public static string GetDefaultTestDir()
        {
            var curDir = Path.GetDirectoryName(typeof(TestRunner).Assembly.Location);
            var testDir = Path.Combine(curDir, "ExpressionTestCases");
            return testDir;
        }

        public string TestRoot { get; set; } = GetDefaultTestDir();

        public void AddDir(string directory = "")
        {
            directory = Path.GetFullPath(directory, TestRoot);
            var allFiles = Directory.EnumerateFiles(directory);

            AddFile(allFiles);
        }

        public void AddFile(params string[] files)
        {
            var x = (IEnumerable<string>)files;
            AddFile(x);
        }

        public void AddFile(IEnumerable<string> files)
        {
            foreach (var file in files)
            {
                AddFile(file);
            }
        }

        // Directive should start with #, end in : like "#SETUP:"
        // Returns true if matched; false if not. Throws on error.
        private static bool ParseDirective(string line, string directive, ref string param)
        {
            if (line.StartsWith(directive, StringComparison.OrdinalIgnoreCase))
            {
                if (param != null)
                {
                    throw new InvalidOperationException($"Can't have multiple {directive}");
                }

                param = line.Substring(directive.Length).Trim();
                return true;
            }

            return false;  
        }

        public void AddFile(string thisFile)
        {
            thisFile = Path.GetFullPath(thisFile, TestRoot);

            var lines = File.ReadAllLines(thisFile);

            // Skip blanks or "comments"
            // >> indicates input expression
            // next line is expected result.

            TestCase test = null;

            var i = -1;

            // Preprocess file directives
            // #Directive: Parameter
            string fileSetup = null;
            string fileOveride = null;
            
            while (i < lines.Length - 1)
            {               
                var line = lines[i + 1];
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//"))
                {
                    i++;
                    continue;
                }

                if (line.Length > 1 && line[0] == '#')
                {
                    string fileDisable = null;
                    if (ParseDirective(line, "#DISABLE:", ref fileDisable))
                    {
                        DisabledFiles.Add(fileDisable);

                        // Will remove all cases in this file.
                        // Can apply to multiple files. 
                        var countRemoved = _tests.RemoveAll(test => string.Equals(Path.GetFileName(test.SourceFile), fileDisable, StringComparison.OrdinalIgnoreCase));                        
                    }
                    else if (ParseDirective(line, "#SETUP:", ref fileSetup) ||
                      ParseDirective(line, "#OVERRIDE:", ref fileOveride))
                    {
                    }
                    else
                    {
                        throw new InvalidOperationException($"Unrecognized directive: {line}");
                    }

                    i++;
                    continue;
                }                

                break;                
            }            

            while (true)
            {
                i++;
                if (i == lines.Length)
                {
                    break;
                }

                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//"))
                {
                    continue;
                }

                if (line.StartsWith(">>"))
                {
                    line = line.Substring(2).Trim();
                    test = new TestCase
                    {
                        Input = line,
                        SourceLine = i + 1, // 1-based
                        SourceFile = thisFile,
                        SetupHandlerName = fileSetup
                    };
                    continue;
                }

                if (test != null)
                {
                    // If it's indented, then part of previous line. 
                    if (line[0] == ' ')
                    {
                        test.Input += "\r\n" + line;
                        continue;
                    }

                    // Line after the input is the response

                    // handle engine-specific results
                    if (line.StartsWith("/*"))
                    {
                        var index = line.IndexOf("*/");
                        if (index > -1)
                        {
                            var engine = line.Substring(2, index - 2).Trim();
                            var result = line.Substring(index + 2).Trim();
                            test.SetExpected(result, engine);
                            continue;
                        }
                    }

                    test.SetExpected(line.Trim());

                    var key = test.GetUniqueId(fileOveride);
                    if (_keyToTests.TryGetValue(key, out var existingTest))
                    {
                        // Updating an existing test. 
                        // Inputs are the same, but update the results.
                        existingTest._expected = test._expected;
                        existingTest.SourceFile = test.SourceFile;
                        existingTest.SourceLine = test.SourceLine;
                    }
                    else
                    {
                        // New test
                        _tests.Add(test);

                        _keyToTests[key] = test;
                    }

                    test = null;
                }
            }
        }

        public (int total, int failed, int passed, string output) RunTests()
        {
            var total = 0;
            var fail = 0;
            var pass = 0;
            var sb = new StringBuilder();

            foreach (var test in _tests)
            {
                foreach (var runner in _runners)
                {
                    total++;

                    var engineName = runner.GetName();

                    // var runner = kv.Value;

                    string actualStr;
                    FormulaValue result = null;
                    var exceptionThrown = false;
                    try
                    {
                        try
                        {
                            result = runner.RunAsync(test.Input, test.SetupHandlerName).Result;
                        }
                        catch (SetupHandlerNotFoundException ex)
                        {
                            sb.AppendLine($"SKIPPED: {engineName}, {Path.GetFileName(test.SourceFile)}:{test.SourceLine}");
                            sb.AppendLine($"SKIPPED: {test.Input}, missing handler: {test.SetupHandlerName}");   
                            continue;
                        }

                        actualStr = TestToString(result);
                    }
                    catch (Exception e)
                    {
                        actualStr = e.Message.Replace("\r\n", "|");
                        exceptionThrown = true;
                    }

                    var expected = test.GetExpected(engineName);
                    if ((exceptionThrown && expected == "Compile Error") || (result != null && expected == "#Error" && runner.IsError(result)))
                    {
                        // Pass!
                        pass++;
                        sb.Append(".");
                        continue;
                    }

                    if (actualStr == expected)
                    {
                        pass++;
                        sb.Append(".");
                    }
                    else
                    {
                        sb.AppendLine();
                        sb.AppendLine($"FAIL: {engineName}, {Path.GetFileName(test.SourceFile)}:{test.SourceLine}");
                        sb.AppendLine($"FAIL: {test.Input}");
                        sb.AppendLine($"expected: {expected}");
                        sb.AppendLine($"actual  : {actualStr}");
                        sb.AppendLine();
                        fail++;
                        continue;
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine($"{total} total. {pass} passed. {fail} failed");
            Console.WriteLine(sb.ToString());
            return (total, fail, pass, sb.ToString());
        }

        public static string TestToString(FormulaValue result)
        {
            var sb = new StringBuilder();
            try
            {
                TestToString(result, sb);
            }
            catch (Exception e)
            {
                // This will cause a diff and test failure below. 
                sb.Append($"<exception writing result: {e.Message}>");
            }

            var actualStr = sb.ToString();
            return actualStr;
        }

        // $$$ Move onto FormulaValue. 
        // Result here should be a string value that could be parsed. 
        // Normalize so we can use this in test cases. 
        internal static void TestToString(FormulaValue result, StringBuilder sb)
        {
            if (result is NumberValue n)
            {
                sb.Append(n.Value);
            }
            else if (result is BooleanValue b)
            {
                sb.Append(b.Value ? "true" : "false");
            }
            else if (result is StringValue s)
            {
                // $$$ proper escaping?
                sb.Append('"' + s.Value + '"');
            }
            else if (result is TableValue t)
            {
                var tableType = (TableType)t.Type;
                var canUseSquareBracketSyntax = t.IsColumn && t.Rows.All(r => r.IsValue) && tableType.GetNames().First().Name == "Value";
                if (canUseSquareBracketSyntax)
                {
                    sb.Append('[');
                }
                else
                {
                    sb.Append("Table(");
                }

                var dil = string.Empty;
                foreach (var row in t.Rows)
                {
                    sb.Append(dil);
                    dil = ",";

                    if (canUseSquareBracketSyntax)
                    {
                        var val = row.Value.Fields.First().Value;
                        TestToString(val, sb);
                    }
                    else
                    {
                        if (row.IsValue)
                        {
                            TestToString(row.Value, sb);
                        }
                        else
                        {
                            TestToString(row.ToFormulaValue(), sb);
                        }
                    }

                    dil = ",";
                }

                if (canUseSquareBracketSyntax)
                {
                    sb.Append(']');
                }
                else
                {
                    sb.Append(')');
                }
            }
            else if (result is RecordValue r)
            {
                var fields = r.Fields.ToArray();
                Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));

                sb.Append('{');
                var dil = string.Empty;

                foreach (var field in fields)
                {
                    sb.Append(dil);
                    sb.Append(field.Name);
                    sb.Append(':');
                    TestToString(field.Value, sb);

                    dil = ",";
                }

                sb.Append('}');
            }
            else if (result is BlankValue)
            {
                sb.Append("Blank()");
            }
            else if (result is ErrorValue)
            {
                sb.Append(result);
            }
            else
            {
                throw new InvalidOperationException($"unsupported value type: {result.GetType().Name}");
            }
        }
    }
}

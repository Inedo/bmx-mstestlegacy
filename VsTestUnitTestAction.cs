﻿using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Inedo.BuildMaster;
using Inedo.BuildMaster.Extensibility.Actions;
using Inedo.BuildMaster.Extensibility.Actions.Testing;
using Inedo.BuildMaster.Extensibility.Agents;
using Inedo.BuildMaster.Files;
using Inedo.BuildMaster.Web;

namespace Inedo.BuildMasterExtensions.MsTest
{
    [ActionProperties(
       "Execute VSTest Tests",
       "Runs VSTest unit tests on a specified test project, recommended for tests in VS 2012 and later.")]
    [Tag(Tags.UnitTests)]
    [CustomEditor(typeof(VsTestUnitTestActionEditor))]
    public sealed class VsTestUnitTestAction : UnitTestActionBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="VsTestUnitTestAction"/> class.
        /// </summary>
        public VsTestUnitTestAction()
        {
        }

        /// <summary>
        /// Gets or sets the test container.
        /// </summary>
        [Persistent]
        public string TestContainer { get; set; }

        /// <summary>
        /// Gets or sets the additional arguments.
        /// </summary>
        [Persistent]
        public string AdditionalArguments { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether [clear existing test results].
        /// </summary>
        [Persistent]
        public bool ClearExistingTestResults { get; set; }

        /// <summary>
        /// Returns a <see cref="System.String" /> that represents this instance.
        /// </summary>
        /// <returns>
        /// A <see cref="System.String" /> that represents this instance.
        /// </returns>
        /// <remarks>
        /// This should return a user-friendly string describing what the Action does
        /// and the state of its important persistent properties.
        /// </remarks>
        public override string ToString()
        {
            return string.Format("Run VSTest tests on {0}", this.TestContainer);
        }

        protected override void RunTests()
        {
            var configurer = this.GetExtensionConfigurer();
            if (configurer == null || string.IsNullOrEmpty(configurer.VsTestPath))
                throw new InvalidOperationException("To run VSTests, the path to vstest.console.exe must be set in the MsTest extension's configuration.");

            var fileOps = this.Context.Agent.GetService<IFileOperationsExecuter>();
            if (!fileOps.FileExists(configurer.VsTestPath))
                throw new FileNotFoundException("Could not find the file vstest.console.exe; ensure that the MsTest extensions's configuration is correct before continuing.", configurer.VsTestPath);

            string resultsPath = fileOps.CombinePath(this.Context.SourceDirectory, "TestResults");

            if (this.ClearExistingTestResults)
            {
                this.LogDebug("Clearing {0} directory...", resultsPath);
                fileOps.ClearFolder(resultsPath);
            }

            this.ExecuteCommandLine(
                configurer.VsTestPath,
                string.Format("{0} /logger:trx {1}", this.TestContainer, this.AdditionalArguments),
                this.Context.SourceDirectory
            );

            if (!fileOps.DirectoryExists(resultsPath))
                throw new DirectoryNotFoundException("Could not find the generated \"TestResults\" directory after running unit tests at: " + resultsPath);

            var resultsDir = fileOps.GetDirectoryEntry(new GetDirectoryEntryCommand()
                {
                    Path = resultsPath,
                    Recurse = false,
                    IncludeRootPath = true
                }
            ).Entry;

            var trxFiles = resultsDir.Files.Where(f => f.Name.EndsWith(".trx")).Cast<ExtendedFileEntryInfo>().ToList();
            if (!trxFiles.Any())
                throw new InvalidOperationException("There are no .trx files in the \"TestResults\" directory.");

            string trxPath = trxFiles
                .Aggregate((latest, next) => next.LastModifiedDate > latest.LastModifiedDate ? next : latest)
                .Path;

            using (var file = fileOps.OpenFile(trxPath, FileMode.Open, FileAccess.Read))
            using (var reader = new XmlTextReader(file) { Namespaces = false })
            {
                var doc = XDocument.Load(reader);
                foreach (var result in doc.Element("TestRun").Element("Results").Elements("UnitTestResult"))
                {
                    string testName = result.Attribute("testName").Value;
                    bool testPassed = result.Attribute("outcome").Value.Equals("Passed", StringComparison.OrdinalIgnoreCase);
                    string testResult;
                    if (testPassed)
                    {
                        testResult = "Passed";
                    }
                    else
                    {
                        var errorInfo = result.Element("Output").Element("ErrorInfo");
                        testResult = errorInfo.Element("Message").Value;
                        var trace = errorInfo.Element("StackTrace");
                        if (trace != null)
                            testResult += Environment.NewLine + trace.Value;
                    }
                    string startDate = result.Attribute("startTime").Value;
                    string endDate = result.Attribute("endTime").Value;

                    this.RecordResult(
                        testName, 
                        testPassed, 
                        testResult,
                        DateTime.Parse(startDate),
                        DateTime.Parse(endDate)
                    );
                }
            }
        }

        private new MsTestConfigurer GetExtensionConfigurer() 
        {
            return (MsTestConfigurer)base.GetExtensionConfigurer();
        }
    }
}
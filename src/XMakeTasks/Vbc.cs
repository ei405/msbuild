﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Resources;
using System.Reflection;
using System.Globalization;
using System.Text.RegularExpressions;

#if (!STANDALONEBUILD)
using Microsoft.Internal.Performance;
#endif

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.Build.Shared;
using Microsoft.Build.Tasks.Hosting;
using Microsoft.Build.Tasks.InteropUtilities;

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// This class defines the "Vbc" XMake task, which enables building assemblies from VB
    /// source files by invoking the VB compiler. This is the new Roslyn XMake task,
    /// meaning that the code is compiled by using the Roslyn compiler server, rather
    /// than vbc.exe. The two should be functionally identical, but the compiler server
    /// should be significantly faster with larger projects and have a smaller memory
    /// footprint.
    /// </summary>
    public class Vbc : ManagedCompiler
    {
        private bool _useHostCompilerIfAvailable = false;

        // The following 1 fields are used, set and re-set in LogEventsFromTextOutput()
        /// <summary>
        /// This stores the origional lines and error priority together in the order in which they were received.
        /// </summary>
        private Queue<VBError> _vbErrorLines = new Queue<VBError>();

        // Used when parsing vbc output to determine the column number of an error
        private bool _isDoneOutputtingErrorMessage = false;
        private int _numberOfLinesInErrorMessage = 0;

        #region Properties

        // Please keep these alphabetized.  These are the parameters specific to Vbc.  The
        // ones shared between Vbc and Csc are defined in ManagedCompiler.cs, which is
        // the base class.

        public string BaseAddress
        {
            set { Bag["BaseAddress"] = value; }
            get { return (string)Bag["BaseAddress"]; }
        }

        public string DisabledWarnings
        {
            set { Bag["DisabledWarnings"] = value; }
            get { return (string)Bag["DisabledWarnings"]; }
        }

        public string DocumentationFile
        {
            set { Bag["DocumentationFile"] = value; }
            get { return (string)Bag["DocumentationFile"]; }
        }

        public string ErrorReport
        {
            set { Bag["ErrorReport"] = value; }
            get { return (string)Bag["ErrorReport"]; }
        }

        public bool GenerateDocumentation
        {
            set { Bag["GenerateDocumentation"] = value; }
            get { return GetBoolParameterWithDefault("GenerateDocumentation", false); }
        }

        public ITaskItem[] Imports
        {
            set { Bag["Imports"] = value; }
            get { return (ITaskItem[])Bag["Imports"]; }
        }

        public string LangVersion
        {
            set { Bag["LangVersion"] = value; }
            get { return (string)Bag["LangVersion"]; }
        }

        public string ModuleAssemblyName
        {
            set { Bag["ModuleAssemblyName"] = value; }
            get { return (string)Bag["ModuleAssemblyName"]; }
        }

        public bool NoStandardLib
        {
            set { Bag["NoStandardLib"] = value; }
            get { return GetBoolParameterWithDefault("NoStandardLib", false); }
        }

        // This is not a documented switch. It prevents the automatic reference to Microsoft.VisualBasic.dll.
        // The VB team believes the only scenario for this is when you are building that assembly itself.
        // We have to support the switch here so that we can build the SDE and VB trees, which need to build this assembly.
        // Although undocumented, it cannot be wrapped with #if BUILDING_DF_LKG because this would prevent dogfood builds
        // within VS, which must use non-LKG msbuild bits.
        public bool NoVBRuntimeReference
        {
            set { Bag["NoVBRuntimeReference"] = value; }
            get { return GetBoolParameterWithDefault("NoVBRuntimeReference", false); }
        }

        public bool NoWarnings
        {
            set { Bag["NoWarnings"] = value; }
            get { return GetBoolParameterWithDefault("NoWarnings", false); }
        }

        public string OptionCompare
        {
            set { Bag["OptionCompare"] = value; }
            get { return (string)Bag["OptionCompare"]; }
        }

        public bool OptionExplicit
        {
            set { Bag["OptionExplicit"] = value; }
            get { return GetBoolParameterWithDefault("OptionExplicit", true); }
        }

        public bool OptionStrict
        {
            set { Bag["OptionStrict"] = value; }
            get { return GetBoolParameterWithDefault("OptionStrict", false); }
        }

        public bool OptionInfer
        {
            set { Bag["OptionInfer"] = value; }
            get { return GetBoolParameterWithDefault("OptionInfer", false); }
        }

        // Currently only /optionstrict:custom
        public string OptionStrictType
        {
            set { Bag["OptionStrictType"] = value; }
            get { return (string)Bag["OptionStrictType"]; }
        }

        public bool RemoveIntegerChecks
        {
            set { Bag["RemoveIntegerChecks"] = value; }
            get { return GetBoolParameterWithDefault("RemoveIntegerChecks", false); }
        }

        public string RootNamespace
        {
            set { Bag["RootNamespace"] = value; }
            get { return (string)Bag["RootNamespace"]; }
        }

        public string SdkPath
        {
            set { Bag["SdkPath"] = value; }
            get { return (string)Bag["SdkPath"]; }
        }

        /// <summary>
        /// Name of the language passed to "/preferreduilang" compiler option.
        /// </summary>
        /// <remarks>
        /// If set to null, "/preferreduilang" option is omitted, and vbc.exe uses its default setting.
        /// Otherwise, the value is passed to "/preferreduilang" as is.
        /// </remarks>
        public string PreferredUILang
        {
            set { Bag["PreferredUILang"] = value; }
            get { return (string)Bag["PreferredUILang"]; }
        }

        public string VsSessionGuid
        {
            set { Bag["VsSessionGuid"] = value; }
            get { return (string)Bag["VsSessionGuid"]; }
        }

        public bool TargetCompactFramework
        {
            set { Bag["TargetCompactFramework"] = value; }
            get { return GetBoolParameterWithDefault("TargetCompactFramework", false); }
        }

        public bool UseHostCompilerIfAvailable
        {
            set { _useHostCompilerIfAvailable = value; }
            get { return _useHostCompilerIfAvailable; }
        }

        public string VBRuntimePath
        {
            set { Bag["VBRuntimePath"] = value; }
            get { return (string)Bag["VBRuntimePath"]; }
        }

        public string Verbosity
        {
            set { Bag["Verbosity"] = value; }
            get { return (string)Bag["Verbosity"]; }
        }

        public string WarningsAsErrors
        {
            set { Bag["WarningsAsErrors"] = value; }
            get { return (string)Bag["WarningsAsErrors"]; }
        }

        public string WarningsNotAsErrors
        {
            set { Bag["WarningsNotAsErrors"] = value; }
            get { return (string)Bag["WarningsNotAsErrors"]; }
        }

        public string VBRuntime
        {
            set { Bag["VBRuntime"] = value; }
            get { return (string)Bag["VBRuntime"]; }
        }

        public string PdbFile
        {
            set { Bag["PdbFile"] = value; }
            get { return (string)Bag["PdbFile"]; }
        }
        #endregion

        #region Tool Members

        /// <summary>
        ///  Return the name of the tool to execute.
        /// </summary>
        override protected string ToolName
        {
            get
            {
                return "vbc2.exe";
            }
        }

        /// <summary>
        /// Override Execute so that we can moved the PDB file if we need to,
        /// after the compiler is done.
        /// </summary>
        public override bool Execute()
        {
            if (!base.Execute())
            {
                return false;
            }

            MovePdbFileIfNecessary(OutputAssembly.ItemSpec);

            return !Log.HasLoggedErrors;
        }

        /// <summary>
        /// Move the PDB file if the PDB file that was generated by the compiler
        /// is not at the specified path, or if it is newer than the one there.
        /// VBC does not have a switch to specify the PDB path, so we are essentially implementing that for it here.
        /// We need make this possible to avoid colliding with the PDB generated by WinMDExp.
        /// 
        /// If at some future point VBC.exe offers a /pdbfile switch, this function can be removed.
        /// </summary>
        internal void MovePdbFileIfNecessary(string outputAssembly)
        {
            // Get the name of the output assembly because the pdb will be written beside it and will have the same name
            if (String.IsNullOrEmpty(PdbFile) || String.IsNullOrEmpty(outputAssembly))
            {
                return;
            }

            try
            {
                string actualPdb = Path.ChangeExtension(outputAssembly, ".pdb"); // This is the pdb that the compiler generated

                FileInfo actualPdbInfo = new FileInfo(actualPdb);

                string desiredLocation = PdbFile;
                if (!desiredLocation.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    desiredLocation += ".pdb";
                }

                FileInfo desiredPdbInfo = new FileInfo(desiredLocation);

                // If the compiler generated a pdb..
                if (actualPdbInfo.Exists)
                {
                    // .. and the desired one does not exist or it's older...
                    if (!desiredPdbInfo.Exists || (desiredPdbInfo.Exists && actualPdbInfo.LastWriteTime > desiredPdbInfo.LastWriteTime))
                    {
                        // Delete the existing one if it's already there, as Move would otherwise fail
                        if (desiredPdbInfo.Exists)
                        {
                            FileUtilities.DeleteNoThrow(desiredPdbInfo.FullName);
                        }

                        // Move the file to where we actually wanted VBC to put it
                        File.Move(actualPdbInfo.FullName, desiredLocation);
                    }
                }
            }
            catch (Exception e) // Catching Exception, but rethrowing unless it's an IO related exception
            {
                if (!ExceptionHandling.IsIoRelatedException(e))
                {
                    throw;
                }

                Log.LogErrorWithCodeFromResources("VBC.RenamePDB", PdbFile, e.Message);
            }
        }

        /// <summary>
        /// Generate the path to the tool
        /// </summary>
        override protected string GenerateFullPathToTool()
        {
            string pathToTool = ToolLocationHelper.GetPathToBuildToolsFile(ToolName, ToolLocationHelper.CurrentToolsVersion);

            if (null == pathToTool)
            {
                pathToTool = ToolLocationHelper.GetPathToDotNetFrameworkFile(ToolName, TargetDotNetFrameworkVersion.VersionLatest);

                if (null == pathToTool)
                {
                    Log.LogErrorWithCodeFromResources("General.FrameworksFileNotFound", ToolName, ToolLocationHelper.GetDotNetFrameworkVersionFolderPrefix(TargetDotNetFrameworkVersion.VersionLatest));
                }
            }

            return pathToTool;
        }

        /// <summary>
        /// vbc.exe only takes the BaseAddress in hexadecimal format.  But we allow the caller
        /// of the task to pass in the BaseAddress in either decimal or hexadecimal format.
        /// Examples of supported hex formats include "0x10000000" or "&amp;H10000000".
        /// </summary>
        internal string GetBaseAddressInHex()
        {
            string originalBaseAddress = this.BaseAddress;

            if (originalBaseAddress != null)
            {
                if (originalBaseAddress.Length > 2)
                {
                    string twoLetterPrefix = originalBaseAddress.Substring(0, 2);

                    if (
                         (0 == String.Compare(twoLetterPrefix, "0x", StringComparison.OrdinalIgnoreCase)) ||
                         (0 == String.Compare(twoLetterPrefix, "&h", StringComparison.OrdinalIgnoreCase))
                       )
                    {
                        // The incoming string is already in hex format ... we just need to
                        // remove the 0x or &H from the beginning.
                        return originalBaseAddress.Substring(2);
                    }
                }

                // The incoming BaseAddress is not in hexadecimal format, so we need to
                // convert it to hex.
                try
                {
                    uint baseAddressDecimal = UInt32.Parse(originalBaseAddress, CultureInfo.InvariantCulture);
                    return baseAddressDecimal.ToString("X", CultureInfo.InvariantCulture);
                }
                catch (FormatException e)
                {
                    ErrorUtilities.VerifyThrowArgument(false, e, "Vbc.ParameterHasInvalidValue", "BaseAddress", originalBaseAddress);
                }
            }

            return null;
        }

        /// <summary>
        /// Looks at all the parameters that have been set, and builds up the string
        /// containing all the command-line switches.
        /// </summary>
        /// <param name="commandLine"></param>
        protected internal override void AddResponseFileCommands(CommandLineBuilderExtension commandLine)
        {
            commandLine.AppendSwitchIfNotNull("/baseaddress:", this.GetBaseAddressInHex());
            commandLine.AppendSwitchIfNotNull("/libpath:", this.AdditionalLibPaths, ",");
            commandLine.AppendSwitchIfNotNull("/imports:", this.Imports, ",");
            // Make sure this /doc+ switch comes *before* the /doc:<file> switch (which is handled in the
            // ManagedCompiler.cs base class).  /doc+ is really just an alias for /doc:<assemblyname>.xml,
            // and the last /doc switch on the command-line wins.  If the user provided a specific doc filename,
            // we want that one to win.
            commandLine.AppendPlusOrMinusSwitch("/doc", this.Bag, "GenerateDocumentation");
            commandLine.AppendSwitchIfNotNull("/optioncompare:", this.OptionCompare);
            commandLine.AppendPlusOrMinusSwitch("/optionexplicit", this.Bag, "OptionExplicit");
            // Make sure this /optionstrict+ switch appears *before* the /optionstrict:xxxx switch below

            /* In Orcas a change was made that set Option Strict-, whenever this.DisabledWarnings was
             * empty.  That was clearly the wrong thing to do and we found it when we had a project with all the warning configuration 
             * entries set to WARNING.  Because this.DisabledWarnings was empty in that case we would end up sending /OptionStrict- 
             * effectively silencing all the warnings that had been selected.
             * 
             * Now what we do is:
             *  If option strict+ is specified, that trumps everything and we just set option strict+ 
             *  Otherwise, just set option strict:custom.
             *  You may wonder why we don't try to set Option Strict-  The reason is that Option Strict- just implies a certain
             *  set of warnings that should be disabled (there's ten of them today)  You get the same effect by sending 
             *  option strict:custom on along with the correct list of disabled warnings.
             *  Rather than make this code know the current set of disabled warnings that comprise Option strict-, we just send
             *  option strict:custom on with the understanding that we'll get the same behavior as option strict- since we are passing
             *  the /nowarn line on that contains all the warnings OptionStrict- would disable anyway. The IDE knows what they are
             *  and puts them in the project file so we are good.  And by not making this code aware of which warnings comprise
             *  Option Strict-, we have one less place we have to keep up to date in terms of what comprises option strict-
             */

            // Decide whether we are Option Strict+ or Option Strict:custom
            object optionStrictSetting = this.Bag["OptionStrict"];
            bool optionStrict = optionStrictSetting != null ? (bool)optionStrictSetting : false;
            if (optionStrict)
            {
                commandLine.AppendSwitch("/optionstrict+");
            }
            else // OptionStrict+ wasn't specified so use :custom.
            {
                commandLine.AppendSwitch("/optionstrict:custom");
            }

            commandLine.AppendSwitchIfNotNull("/optionstrict:", this.OptionStrictType);
            commandLine.AppendWhenTrue("/nowarn", this.Bag, "NoWarnings");
            commandLine.AppendSwitchWithSplitting("/nowarn:", this.DisabledWarnings, ",", ';', ',');
            commandLine.AppendPlusOrMinusSwitch("/optioninfer", this.Bag, "OptionInfer");
            commandLine.AppendWhenTrue("/nostdlib", this.Bag, "NoStandardLib");
            commandLine.AppendWhenTrue("/novbruntimeref", this.Bag, "NoVBRuntimeReference");
            commandLine.AppendSwitchIfNotNull("/errorreport:", this.ErrorReport);
            commandLine.AppendSwitchIfNotNull("/platform:", this.PlatformWith32BitPreference);
            commandLine.AppendPlusOrMinusSwitch("/removeintchecks", this.Bag, "RemoveIntegerChecks");
            commandLine.AppendSwitchIfNotNull("/rootnamespace:", this.RootNamespace);
            commandLine.AppendSwitchIfNotNull("/sdkpath:", this.SdkPath);
            commandLine.AppendSwitchIfNotNull("/langversion:", this.LangVersion);
            commandLine.AppendSwitchIfNotNull("/moduleassemblyname:", this.ModuleAssemblyName);
            commandLine.AppendWhenTrue("/netcf", this.Bag, "TargetCompactFramework");
            commandLine.AppendSwitchIfNotNull("/preferreduilang:", this.PreferredUILang);
            commandLine.AppendPlusOrMinusSwitch("/highentropyva", this.Bag, "HighEntropyVA");

            if (0 == String.Compare(this.VBRuntimePath, this.VBRuntime, StringComparison.OrdinalIgnoreCase))
            {
                commandLine.AppendSwitchIfNotNull("/vbruntime:", this.VBRuntimePath);
            }
            else if (this.VBRuntime != null)
            {
                string vbRuntimeSwitch = this.VBRuntime;
                if (0 == String.Compare(vbRuntimeSwitch, "EMBED", StringComparison.OrdinalIgnoreCase))
                {
                    commandLine.AppendSwitch("/vbruntime*");
                }
                else if (0 == String.Compare(vbRuntimeSwitch, "NONE", StringComparison.OrdinalIgnoreCase))
                {
                    commandLine.AppendSwitch("/vbruntime-");
                }
                else if (0 == String.Compare(vbRuntimeSwitch, "DEFAULT", StringComparison.OrdinalIgnoreCase))
                {
                    commandLine.AppendSwitch("/vbruntime+");
                }
                else
                {
                    commandLine.AppendSwitchIfNotNull("/vbruntime:", vbRuntimeSwitch);
                }
            }


            // Verbosity
            if (
                   (this.Verbosity != null) &&

                   (
                      (0 == String.Compare(this.Verbosity, "quiet", StringComparison.OrdinalIgnoreCase)) ||
                      (0 == String.Compare(this.Verbosity, "verbose", StringComparison.OrdinalIgnoreCase))
                   )
                )
            {
                commandLine.AppendSwitchIfNotNull("/", this.Verbosity);
            }

            commandLine.AppendSwitchIfNotNull("/doc:", this.DocumentationFile);
            commandLine.AppendSwitchUnquotedIfNotNull("/define:", Vbc.GetDefineConstantsSwitch(this.DefineConstants));
            AddReferencesToCommandLine(commandLine);
            commandLine.AppendSwitchIfNotNull("/win32resource:", this.Win32Resource);

            // Special case for "Sub Main"
            if (0 != String.Compare("Sub Main", this.MainEntryPoint, StringComparison.OrdinalIgnoreCase))
            {
                commandLine.AppendSwitchIfNotNull("/main:", this.MainEntryPoint);
            }

            base.AddResponseFileCommands(commandLine);

            // This should come after the "TreatWarningsAsErrors" flag is processed (in managedcompiler.cs).
            // Because if TreatWarningsAsErrors=false, then we'll have a /warnaserror- on the command-line,
            // and then any specific warnings that should be treated as errors should be specified with
            // /warnaserror+:<list> after the /warnaserror- switch.  The order of the switches on the command-line
            // does matter.
            //
            // Note that
            //      /warnaserror+
            // is just shorthand for:
            //      /warnaserror+:<all possible warnings>
            //
            // Similarly,
            //      /warnaserror-
            // is just shorthand for:
            //      /warnaserror-:<all possible warnings>
            commandLine.AppendSwitchWithSplitting("/warnaserror+:", this.WarningsAsErrors, ",", ';', ',');
            commandLine.AppendSwitchWithSplitting("/warnaserror-:", this.WarningsNotAsErrors, ",", ';', ',');

            // If not design time build and the globalSessionGuid property was set then add a -globalsessionguid:<guid>
            bool designTime = false;
            if (this.HostObject != null)
            {
                var vbHost = this.HostObject as IVbcHostObject;
                designTime = vbHost.IsDesignTime();
            }
            if (!designTime)
            {
                if (!string.IsNullOrWhiteSpace(this.VsSessionGuid))
                {
                    commandLine.AppendSwitchIfNotNull("/sqmsessionguid:", this.VsSessionGuid);
                }
            }

            // It's a good idea for the response file to be the very last switch passed, just 
            // from a predictability perspective.
            if (this.ResponseFiles != null)
            {
                foreach (ITaskItem response in this.ResponseFiles)
                {
                    commandLine.AppendSwitchIfNotNull("@", response.ItemSpec);
                }
            }
        }

        private void AddReferencesToCommandLine
            (
            CommandLineBuilderExtension commandLine
            )
        {
            if ((this.References == null) || (this.References.Length == 0))
            {
                return;
            }

            List<ITaskItem> references = new List<ITaskItem>(this.References.Length);
            List<ITaskItem> links = new List<ITaskItem>(this.References.Length);

            foreach (ITaskItem reference in this.References)
            {
                bool embed = MetadataConversionUtilities.TryConvertItemMetadataToBool
                    (
                        reference,
                        ItemMetadataNames.embedInteropTypes
                    );

                if (embed)
                {
                    links.Add(reference);
                }
                else
                {
                    references.Add(reference);
                }
            }

            if (links.Count > 0)
            {
                commandLine.AppendSwitchIfNotNull("/link:", links.ToArray(), ",");
            }

            if (references.Count > 0)
            {
                commandLine.AppendSwitchIfNotNull("/reference:", references.ToArray(), ",");
            }
        }

        /// <summary>
        /// Validate parameters, log errors and warnings and return true if
        /// Execute should proceed.
        /// </summary>
        override protected bool ValidateParameters()
        {
            if (!base.ValidateParameters())
            {
                return false;
            }

            // Validate that the "Verbosity" parameter is one of "quiet", "normal", or "verbose".
            if (this.Verbosity != null)
            {
                if ((0 != String.Compare(Verbosity, "normal", StringComparison.OrdinalIgnoreCase)) &&
                    (0 != String.Compare(Verbosity, "quiet", StringComparison.OrdinalIgnoreCase)) &&
                    (0 != String.Compare(Verbosity, "verbose", StringComparison.OrdinalIgnoreCase)))
                {
                    Log.LogErrorWithCodeFromResources("Vbc.EnumParameterHasInvalidValue", "Verbosity", this.Verbosity, "Quiet, Normal, Verbose");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// This method intercepts the lines to be logged coming from STDOUT from VBC.
        /// Once we see a standard vb warning or error, then we capture it and grab the next 3
        /// lines so we can transform the string form the form of FileName.vb(line) to FileName.vb(line,column)
        /// which will allow us to report the line and column to the IDE, and thus filter the error
        /// in the duplicate case for multi-targeting, or just squiggle the appropriate token 
        /// instead of the entire line.
        /// </summary>
        /// <param name="singleLine">A single line from the STDOUT of the vbc compiler</param>
        /// <param name="messageImportance">High,Low,Normal</param>
        protected override void LogEventsFromTextOutput(string singleLine, MessageImportance messageImportance)
        {
            // We can return immediately if this was not called by the out of proc compiler
            if (!this.UsedCommandLineTool)
            {
                base.LogEventsFromTextOutput(singleLine, messageImportance);
                return;
            }

            // We can also return immediately if the current string is not a warning or error
            // and we have not seen a warning or error yet. 'Error' and 'Warning' are not localized.
            if (_vbErrorLines.Count == 0 &&
                singleLine.IndexOf("warning", StringComparison.OrdinalIgnoreCase) == -1 &&
                singleLine.IndexOf("error", StringComparison.OrdinalIgnoreCase) == -1)
            {
                base.LogEventsFromTextOutput(singleLine, messageImportance);
                return;
            }

            ParseVBErrorOrWarning(singleLine, messageImportance);
        }

        /// <summary>
        /// Given a string, parses it to find out whether it's an error or warning and, if so,
        /// make sure it's validated properly.  
        /// </summary>
        /// <comments>
        /// INTERNAL FOR UNITTESTING ONLY
        /// </comments>
        /// <param name="singleLine">The line to parse</param>
        /// <param name="messageImportance">The MessageImportance to use when reporting the error.</param>
        internal void ParseVBErrorOrWarning(string singleLine, MessageImportance messageImportance)
        {
            // if this string is empty then we haven't seen the first line of an error yet
            if (_vbErrorLines.Count > 0)
            {
                // vbc separates the error message from the source text with an empty line, so
                // we can check for an empty line to see if vbc finished outputting the error message
                if (!_isDoneOutputtingErrorMessage && singleLine.Length == 0)
                {
                    _isDoneOutputtingErrorMessage = true;
                    _numberOfLinesInErrorMessage = _vbErrorLines.Count;
                }

                _vbErrorLines.Enqueue(new VBError(singleLine, messageImportance));

                // We are looking for the line that indicates the column (contains the '~'),
                // which vbc outputs 3 lines below the error message:
                //
                // <error message>
                // <blank line>
                // <line with the source text>
                // <line with the '~'>
                if (_isDoneOutputtingErrorMessage &&
                    _vbErrorLines.Count == _numberOfLinesInErrorMessage + 3)
                {
                    // Once we have the 4th line (error line + 3), then parse it for the first ~
                    // which will correspond to the column of the token with the error because
                    // VBC respects the users's indentation settings in the file it is compiling
                    // and only outputs SPACE chars to STDOUT.

                    // The +1 is to translate the index into user columns which are 1 based.

                    VBError originalVBError = _vbErrorLines.Dequeue();
                    string originalVBErrorString = originalVBError.Message;

                    int column = singleLine.IndexOf('~') + 1;
                    int endParenthesisLocation = originalVBErrorString.IndexOf(')');

                    // If for some reason the line does not contain any ~ then something went wrong
                    // so abort and return the origional string.
                    if (column < 0 || endParenthesisLocation < 0)
                    {
                        // we need to output all of the original lines we ate.
                        Log.LogMessageFromText(originalVBErrorString, originalVBError.MessageImportance);
                        foreach (VBError vberror in _vbErrorLines)
                        {
                            base.LogEventsFromTextOutput(vberror.Message, vberror.MessageImportance);
                        }

                        _vbErrorLines.Clear();
                        return;
                    }

                    string newLine = null;
                    newLine = originalVBErrorString.Substring(0, endParenthesisLocation) + "," + column + originalVBErrorString.Substring(endParenthesisLocation);

                    // Output all of the lines of the error, but with the modified first line as well.
                    Log.LogMessageFromText(newLine, originalVBError.MessageImportance);
                    foreach (VBError vberror in _vbErrorLines)
                    {
                        base.LogEventsFromTextOutput(vberror.Message, vberror.MessageImportance);
                    }

                    _vbErrorLines.Clear();
                }
            }
            else
            {
                CanonicalError.Parts parts = CanonicalError.Parse(singleLine);
                if (parts == null)
                {
                    base.LogEventsFromTextOutput(singleLine, messageImportance);
                }
                else if ((parts.category == CanonicalError.Parts.Category.Error ||
                     parts.category == CanonicalError.Parts.Category.Warning) &&
                     parts.column == CanonicalError.Parts.numberNotSpecified)
                {
                    if (parts.line != CanonicalError.Parts.numberNotSpecified)
                    {
                        // If we got here, then this is a standard VBC error or warning.
                        _vbErrorLines.Enqueue(new VBError(singleLine, messageImportance));
                        _isDoneOutputtingErrorMessage = false;
                        _numberOfLinesInErrorMessage = 0;
                    }
                    else
                    {
                        // Project-level errors don't have line numbers -- just output now. 
                        base.LogEventsFromTextOutput(singleLine, messageImportance);
                    }
                }
            }
        }

        #endregion

        /// <summary>
        /// Many VisualStudio VB projects have values for the DefineConstants property that
        /// contain quotes and spaces.  Normally we don't allow parameters passed into the
        /// task to contain quotes, because if we weren't careful, we might accidently
        /// allow a parameter injection attach.  But for "DefineConstants", we have to allow
        /// it.
        /// So this method prepares the string to be passed in on the /define: command-line
        /// switch.  It does that by quoting the entire string, and escaping the embedded
        /// quotes.
        /// </summary>
        internal static string GetDefineConstantsSwitch
            (
            string originalDefineConstants
            )
        {
            if ((originalDefineConstants == null) || (originalDefineConstants.Length == 0))
            {
                return null;
            }

            StringBuilder finalDefineConstants = new StringBuilder(originalDefineConstants);

            // Replace slash-quote with slash-slash-quote.
            finalDefineConstants.Replace("\\\"", "\\\\\"");

            // Replace quote with slash-quote.
            finalDefineConstants.Replace("\"", "\\\"");

            // Surround the whole thing with a pair of double-quotes.
            finalDefineConstants.Insert(0, '"');
            finalDefineConstants.Append('"');

            // Now it's ready to be passed in to the /define: switch.
            return finalDefineConstants.ToString();
        }

        /// <summary>
        /// This method will initialize the host compiler object with all the switches,
        /// parameters, resources, references, sources, etc.
        ///
        /// It returns true if everything went according to plan.  It returns false if the
        /// host compiler had a problem with one of the parameters that was passed in.
        /// 
        /// This method also sets the "this.HostCompilerSupportsAllParameters" property
        /// accordingly.
        ///
        /// Example:
        ///     If we attempted to pass in Platform="foo", then this method would
        ///     set HostCompilerSupportsAllParameters=true, but it would throw an 
        ///     exception because the host compiler fully supports
        ///     the Platform parameter, but "foo" is an illegal value.
        ///
        /// Example:
        ///     If we attempted to pass in NoConfig=false, then this method would set
        ///     HostCompilerSupportsAllParameters=false, because while this is a legal
        ///     thing for csc.exe, the IDE compiler cannot support it.  In this situation
        ///     the return value will also be false.
        /// </summary>
        private bool InitializeHostCompiler
            (
            // NOTE: For compat reasons this must remain IVbcHostObject
            // we can dynamically test for smarter interfaces later..
            IVbcHostObject vbcHostObject
            )
        {
            this.HostCompilerSupportsAllParameters = this.UseHostCompilerIfAvailable;
            string param = "Unknown";

            try
            {
                param = "BeginInitialization";
                vbcHostObject.BeginInitialization();

                param = "AdditionalLibPaths"; this.CheckHostObjectSupport(param, vbcHostObject.SetAdditionalLibPaths(this.AdditionalLibPaths));
                param = "AddModules"; this.CheckHostObjectSupport(param, vbcHostObject.SetAddModules(this.AddModules));

                // For host objects which support them, set the analyzers, ruleset and additional files.
                IAnalyzerHostObject analyzerHostObject = vbcHostObject as IAnalyzerHostObject;
                if (analyzerHostObject != null)
                {
                    param = "Analyzers"; this.CheckHostObjectSupport(param, analyzerHostObject.SetAnalyzers(this.Analyzers));
                    param = "CodeAnalysisRuleSet"; this.CheckHostObjectSupport(param, analyzerHostObject.SetRuleSet(this.CodeAnalysisRuleSet));
                    param = "AdditionalFiles"; this.CheckHostObjectSupport(param, analyzerHostObject.SetAdditionalFiles(this.AdditionalFiles));
                }

                param = "BaseAddress"; this.CheckHostObjectSupport(param, vbcHostObject.SetBaseAddress(this.TargetType, this.GetBaseAddressInHex()));
                param = "CodePage"; this.CheckHostObjectSupport(param, vbcHostObject.SetCodePage(this.CodePage));
                param = "DebugType"; this.CheckHostObjectSupport(param, vbcHostObject.SetDebugType(this.EmitDebugInformation, this.DebugType));
                param = "DefineConstants"; this.CheckHostObjectSupport(param, vbcHostObject.SetDefineConstants(this.DefineConstants));
                param = "DelaySign"; this.CheckHostObjectSupport(param, vbcHostObject.SetDelaySign(this.DelaySign));
                param = "DocumentationFile"; this.CheckHostObjectSupport(param, vbcHostObject.SetDocumentationFile(this.DocumentationFile));
                param = "FileAlignment"; this.CheckHostObjectSupport(param, vbcHostObject.SetFileAlignment(this.FileAlignment));
                param = "GenerateDocumentation"; this.CheckHostObjectSupport(param, vbcHostObject.SetGenerateDocumentation(this.GenerateDocumentation));
                param = "Imports"; this.CheckHostObjectSupport(param, vbcHostObject.SetImports(this.Imports));
                param = "KeyContainer"; this.CheckHostObjectSupport(param, vbcHostObject.SetKeyContainer(this.KeyContainer));
                param = "KeyFile"; this.CheckHostObjectSupport(param, vbcHostObject.SetKeyFile(this.KeyFile));
                param = "LinkResources"; this.CheckHostObjectSupport(param, vbcHostObject.SetLinkResources(this.LinkResources));
                param = "MainEntryPoint"; this.CheckHostObjectSupport(param, vbcHostObject.SetMainEntryPoint(this.MainEntryPoint));
                param = "NoConfig"; this.CheckHostObjectSupport(param, vbcHostObject.SetNoConfig(this.NoConfig));
                param = "NoStandardLib"; this.CheckHostObjectSupport(param, vbcHostObject.SetNoStandardLib(this.NoStandardLib));
                param = "NoWarnings"; this.CheckHostObjectSupport(param, vbcHostObject.SetNoWarnings(this.NoWarnings));
                param = "Optimize"; this.CheckHostObjectSupport(param, vbcHostObject.SetOptimize(this.Optimize));
                param = "OptionCompare"; this.CheckHostObjectSupport(param, vbcHostObject.SetOptionCompare(this.OptionCompare));
                param = "OptionExplicit"; this.CheckHostObjectSupport(param, vbcHostObject.SetOptionExplicit(this.OptionExplicit));
                param = "OptionStrict"; this.CheckHostObjectSupport(param, vbcHostObject.SetOptionStrict(this.OptionStrict));
                param = "OptionStrictType"; this.CheckHostObjectSupport(param, vbcHostObject.SetOptionStrictType(this.OptionStrictType));
                param = "OutputAssembly"; this.CheckHostObjectSupport(param, vbcHostObject.SetOutputAssembly(this.OutputAssembly.ItemSpec));

                // For host objects which support them, set platform with 32BitPreference, HighEntropyVA, and SubsystemVersion
                IVbcHostObject5 vbcHostObject5 = vbcHostObject as IVbcHostObject5;
                if (vbcHostObject5 != null)
                {
                    param = "PlatformWith32BitPreference"; this.CheckHostObjectSupport(param, vbcHostObject5.SetPlatformWith32BitPreference(this.PlatformWith32BitPreference));
                    param = "HighEntropyVA"; this.CheckHostObjectSupport(param, vbcHostObject5.SetHighEntropyVA(this.HighEntropyVA));
                    param = "SubsystemVersion"; this.CheckHostObjectSupport(param, vbcHostObject5.SetSubsystemVersion(this.SubsystemVersion));
                }
                else
                {
                    param = "Platform"; this.CheckHostObjectSupport(param, vbcHostObject.SetPlatform(this.Platform));
                }

                param = "References"; this.CheckHostObjectSupport(param, vbcHostObject.SetReferences(this.References));
                param = "RemoveIntegerChecks"; this.CheckHostObjectSupport(param, vbcHostObject.SetRemoveIntegerChecks(this.RemoveIntegerChecks));
                param = "Resources"; this.CheckHostObjectSupport(param, vbcHostObject.SetResources(this.Resources));
                param = "ResponseFiles"; this.CheckHostObjectSupport(param, vbcHostObject.SetResponseFiles(this.ResponseFiles));
                param = "RootNamespace"; this.CheckHostObjectSupport(param, vbcHostObject.SetRootNamespace(this.RootNamespace));
                param = "SdkPath"; this.CheckHostObjectSupport(param, vbcHostObject.SetSdkPath(this.SdkPath));
                param = "Sources"; this.CheckHostObjectSupport(param, vbcHostObject.SetSources(this.Sources));
                param = "TargetCompactFramework"; this.CheckHostObjectSupport(param, vbcHostObject.SetTargetCompactFramework(this.TargetCompactFramework));
                param = "TargetType"; this.CheckHostObjectSupport(param, vbcHostObject.SetTargetType(this.TargetType));
                param = "TreatWarningsAsErrors"; this.CheckHostObjectSupport(param, vbcHostObject.SetTreatWarningsAsErrors(this.TreatWarningsAsErrors));
                param = "WarningsAsErrors"; this.CheckHostObjectSupport(param, vbcHostObject.SetWarningsAsErrors(this.WarningsAsErrors));
                param = "WarningsNotAsErrors"; this.CheckHostObjectSupport(param, vbcHostObject.SetWarningsNotAsErrors(this.WarningsNotAsErrors));
                // DisabledWarnings needs to come after WarningsAsErrors and WarningsNotAsErrors, because
                // of the way the host object works, and the fact that DisabledWarnings trump Warnings[Not]AsErrors.
                param = "DisabledWarnings"; this.CheckHostObjectSupport(param, vbcHostObject.SetDisabledWarnings(this.DisabledWarnings));
                param = "Win32Icon"; this.CheckHostObjectSupport(param, vbcHostObject.SetWin32Icon(this.Win32Icon));
                param = "Win32Resource"; this.CheckHostObjectSupport(param, vbcHostObject.SetWin32Resource(this.Win32Resource));

                // In order to maintain compatibility with previous host compilers, we must
                // light-up for IVbcHostObject2
                if (vbcHostObject is IVbcHostObject2)
                {
                    IVbcHostObject2 vbcHostObject2 = (IVbcHostObject2)vbcHostObject;
                    param = "ModuleAssemblyName"; this.CheckHostObjectSupport(param, vbcHostObject2.SetModuleAssemblyName(this.ModuleAssemblyName));
                    param = "OptionInfer"; this.CheckHostObjectSupport(param, vbcHostObject2.SetOptionInfer(this.OptionInfer));
                    param = "Win32Manifest"; this.CheckHostObjectSupport(param, vbcHostObject2.SetWin32Manifest(this.GetWin32ManifestSwitch(this.NoWin32Manifest, this.Win32Manifest)));
                    // initialize option Infer
                    CheckHostObjectSupport("OptionInfer", vbcHostObject2.SetOptionInfer(this.OptionInfer));
                }
                else
                {
                    // If we have been given a property that the host compiler doesn't support
                    // then we need to state that we are falling back to the command line compiler
                    if (!String.IsNullOrEmpty(ModuleAssemblyName))
                    {
                        CheckHostObjectSupport("ModuleAssemblyName", false);
                    }

                    if (Bag.ContainsKey("OptionInfer"))
                    {
                        CheckHostObjectSupport("OptionInfer", false);
                    }

                    if (!String.IsNullOrEmpty(Win32Manifest))
                    {
                        CheckHostObjectSupport("Win32Manifest", false);
                    }
                }

                // Check for support of the LangVersion property
                if (vbcHostObject is IVbcHostObject3)
                {
                    IVbcHostObject3 vbcHostObject3 = (IVbcHostObject3)vbcHostObject;
                    param = "LangVersion"; this.CheckHostObjectSupport(param, vbcHostObject3.SetLanguageVersion(this.LangVersion));
                }
                else if (!String.IsNullOrEmpty(this.LangVersion) && !this.UsedCommandLineTool)
                {
                    CheckHostObjectSupport("LangVersion", false);
                }

                if (vbcHostObject is IVbcHostObject4)
                {
                    IVbcHostObject4 vbcHostObject4 = (IVbcHostObject4)vbcHostObject;
                    param = "VBRuntime"; this.CheckHostObjectSupport(param, vbcHostObject4.SetVBRuntime(this.VBRuntime));
                }
                // Support for NoVBRuntimeReference was added to this task after IVbcHostObject was frozen. That doesn't matter much because the host
                // compiler doesn't support it, and almost nobody uses it anyway. But if someone has set it, we need to hard code falling back to
                // the command line compiler here.
                if (NoVBRuntimeReference)
                {
                    CheckHostObjectSupport("NoVBRuntimeReference", false);
                }

                // In general, we don't support preferreduilang with the in-proc compiler.  It will always use the same locale as the
                // host process, so in general, we have to fall back to the command line compiler if this option is specified.
                // However, we explicitly allow two values (mostly for parity with C#):
                // Null is supported because it means that option should be omitted, and compiler default used - obviously always valid.
                // Explicitly specified name of current locale is also supported, since it is effectively a no-op.
                if (!String.IsNullOrEmpty(PreferredUILang) && !String.Equals(PreferredUILang, System.Globalization.CultureInfo.CurrentUICulture.Name, StringComparison.OrdinalIgnoreCase))
                {
                    CheckHostObjectSupport("PreferredUILang", false);
                }
            }
            catch (Exception e)
            {
                if (ExceptionHandling.IsCriticalException(e))
                {
                    throw;
                }

                if (this.HostCompilerSupportsAllParameters)
                {
                    // If the host compiler doesn't support everything we need, we're going to end up 
                    // shelling out to the command-line compiler anyway.  That means the command-line
                    // compiler will log the error.  So here, we only log the error if we would've
                    // tried to use the host compiler.
                    Log.LogErrorWithCodeFromResources("General.CouldNotSetHostObjectParameter", param, e.Message);
                }

                return false;
            }
            finally
            {
                // In the case of the VB host compiler, the EndInitialization method will
                // throw (due to FAILED HRESULT) if there was a bad value for one of the
                // parameters.
                vbcHostObject.EndInitialization();
            }

            return true;
        }

        /// <summary>
        /// This method will get called during Execute() if a host object has been passed into the Vbc
        /// task.  Returns one of the following values to indicate what the next action should be:
        ///     UseHostObjectToExecute          Host compiler exists and was initialized.
        ///     UseAlternateToolToExecute       Host compiler doesn't exist or was not appropriate.
        ///     NoActionReturnSuccess           Host compiler was already up-to-date, and we're done.
        ///     NoActionReturnFailure           Bad parameters were passed into the task.
        /// </summary>
        override protected HostObjectInitializationStatus InitializeHostObject()
        {
            if (this.HostObject != null)
            {
                // When the host object was passed into the task, it was passed in as a generic
                // "Object" (because ITask interface obviously can't have any Vbc-specific stuff
                // in it, and each task is going to want to communicate with its host in a unique
                // way).  Now we cast it to the specific type that the Vbc task expects.  If the
                // host object does not match this type, the host passed in an invalid host object
                // to Vbc, and we error out.

                // NOTE: For compat reasons this must remain IVbcHostObject
                // we can dynamically test for smarter interfaces later..
                using (RCWForCurrentContext<IVbcHostObject> hostObject = new RCWForCurrentContext<IVbcHostObject>(this.HostObject as IVbcHostObject))
                {
                    IVbcHostObject vbcHostObject = hostObject.RCW;

                    if (vbcHostObject != null)
                    {
                        bool hostObjectSuccessfullyInitialized = InitializeHostCompiler(vbcHostObject);

                        // If we're currently only in design-time (as opposed to build-time),
                        // then we're done.  We've initialized the host compiler as best we
                        // can, and we certainly don't want to actually do the final compile.
                        // So return true, saying we're done and successful.
                        if (vbcHostObject.IsDesignTime())
                        {
                            // If we are design-time then we do not want to continue the build at 
                            // this time.
                            return hostObjectSuccessfullyInitialized ?
                                HostObjectInitializationStatus.NoActionReturnSuccess :
                                HostObjectInitializationStatus.NoActionReturnFailure;
                        }

                        if (!this.HostCompilerSupportsAllParameters || UseAlternateCommandLineToolToExecute())
                        {
                            // Since the host compiler has refused to take on the responsibility for this compilation,
                            // we're about to shell out to the command-line compiler to handle it.  If some of the
                            // references don't exist on disk, we know the command-line compiler will fail, so save
                            // the trouble, and just throw a consistent error ourselves.  This allows us to give
                            // more information than the compiler would, and also make things consistent across
                            // Vbc / Csc / etc. 
                            // This suite behaves differently in localized builds than on English builds because 
                            // VBC.EXE doesn't localize the word "error" when they emit errors and so we can't scan for it.
                            if (!CheckAllReferencesExistOnDisk())
                            {
                                return HostObjectInitializationStatus.NoActionReturnFailure;
                            }

                            // The host compiler doesn't support some of the switches/parameters
                            // being passed to it.  Therefore, we resort to using the command-line compiler
                            // in this case.
                            UsedCommandLineTool = true;
                            return HostObjectInitializationStatus.UseAlternateToolToExecute;
                        }

                        // Ok, by now we validated that the host object supports the necessary switches
                        // and parameters.  Last thing to check is whether the host object is up to date,
                        // and in that case, we will inform the caller that no further action is necessary.
                        if (hostObjectSuccessfullyInitialized)
                        {
                            return vbcHostObject.IsUpToDate() ?
                                HostObjectInitializationStatus.NoActionReturnSuccess :
                                HostObjectInitializationStatus.UseHostObjectToExecute;
                        }
                        else
                        {
                            return HostObjectInitializationStatus.NoActionReturnFailure;
                        }
                    }
                    else
                    {
                        Log.LogErrorWithCodeFromResources("General.IncorrectHostObject", "Vbc", "IVbcHostObject");
                    }
                }
            }


            // No appropriate host object was found.
            UsedCommandLineTool = true;
            return HostObjectInitializationStatus.UseAlternateToolToExecute;
        }

        /// <summary>
        /// This method will get called during Execute() if a host object has been passed into the Vbc
        /// task.  Returns true if an appropriate host object was found, it was called to do the compile,
        /// and the compile succeeded.  Otherwise, we return false.
        /// </summary>
        override protected bool CallHostObjectToExecute()
        {
            Debug.Assert(this.HostObject != null, "We should not be here if the host object has not been set.");

            IVbcHostObject vbcHostObject = this.HostObject as IVbcHostObject;
            Debug.Assert(vbcHostObject != null, "Wrong kind of host object passed in!");
            try
            {
#if (!STANDALONEBUILD)
                CodeMarkers.Instance.CodeMarker(CodeMarkerEvent.perfMSBuildHostCompileBegin); 
#endif
                IVbcHostObject5 vbcHostObject5 = vbcHostObject as IVbcHostObject5;
                Debug.Assert(vbcHostObject5 != null, "Wrong kind of host object passed in!");

                // IVbcHostObjectFreeThreaded::Compile is the preferred way to compile the host object
                // because while it is still synchronous it does its waiting on our BG thread 
                // (as opposed to the UI thread for IVbcHostObject::Compile)
                if (vbcHostObject5 != null)
                {
                    IVbcHostObjectFreeThreaded freeThreadedHostObject = vbcHostObject5.GetFreeThreadedHostObject();
                    return freeThreadedHostObject.Compile();
                }
                else
                {
                    // If for some reason we can't get to IVbcHostObject5 we just fall back to the old
                    // Compile method. This method unfortunately allows for reentrancy on the UI thread.
                    return vbcHostObject.Compile();
                }
            }
            finally
            {
#if (!STANDALONEBUILD)
                CodeMarkers.Instance.CodeMarker(CodeMarkerEvent.perfMSBuildHostCompileEnd);
#endif
            }
        }

        /// <summary>
        /// private class that just holds together name, value pair for the vbErrorLines Queue
        /// </summary>
        private class VBError
        {
            public string Message { get; set; }
            public MessageImportance MessageImportance { get; set; }

            public VBError(string message, MessageImportance importance)
            {
                this.Message = message;
                this.MessageImportance = importance;
            }
        }
    }
}

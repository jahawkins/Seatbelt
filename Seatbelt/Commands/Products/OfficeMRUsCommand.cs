﻿#nullable disable
using Microsoft.Win32;
using Seatbelt.Util;
using System;
using System.Collections.Generic;
using Seatbelt.Output.TextWriters;
using Seatbelt.Output.Formatters;
using Seatbelt.Interop;
using System.Linq;
using System.Text.RegularExpressions;
using System.Globalization;
using System.IO;
using VBA = Microsoft.Vbe.Interop;
using Excel = Microsoft.Office.Interop.Excel;
using Word = Microsoft.Office.Interop.Word;
using System.Threading;

namespace Seatbelt.Commands.Windows
{
    class OfficeMRU
    {
        public string Product { get; set; }

        public string Type { get; set; }

        public string FileName { get; set; }
    }

    class MacroModule
    {
        public string Name { get; set; }

        public string Code { get; set; }
    }

    enum RegistryBlock
    {
        // Overall, if (int) RegistryBlock > 1 then a change happened and we need to warn and take action to revert

        // If unblocked, no changes
        UNBLOCKED, // 0
        // If missing the whole key, no changes
        MISSING_KEY, // 1
        // If blocked a change will happen
        BLOCKED, // 2 
        // If missining the DWORD value, a change will happen
        MISSING_DWORD // 3
    }

    internal class OfficeMRUsCommand : CommandBase
    {
        public override string Command => "OfficeMRUs";
        public override string Description => "Office most recently used file list (last 7 days)";
        public override CommandGroup[] Group => new[] { CommandGroup.User };
        public override bool SupportRemote => false; // TODO but a bit more complicated

        public OfficeMRUsCommand(Runtime runtime) : base(runtime)
        {
        }


        private void SetRegistryKey(string application, string keyVal)
        {
            for (int i = 7; i <= 16; i++)
            {
                string version = i.ToString() + ".0";
                RegistryKey vbomKey = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\Office\{version}\{application}\Security", true);
                if (vbomKey != null)
                {
                    if (vbomKey.GetValueNames().Contains("AccessVBOM"))
                    {
                        string keyPath = vbomKey.ToString() + @"\AccessVBOM";
                        WriteWarning($"Setting {keyPath} to {keyVal}");
                        vbomKey.SetValue("AccessVBOM", keyVal, Microsoft.Win32.RegistryValueKind.DWord);
                        vbomKey.Close();
                    }
                }
            }
        }

        private void DeleteRegistryKey(string application)
        {
            for (int i = 7; i <= 16; i++)
            {
                string version = i.ToString() + ".0";
                RegistryKey vbomKey = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\Office\{version}\{application}\Security", true);
                if (vbomKey != null)
                {
                    if (vbomKey.GetValueNames().Contains("AccessVBOM"))
                    {
                        string keyPath = vbomKey.ToString() + @"\AccessVBOM";
                        WriteWarning($"Deleting {keyPath}");
                        vbomKey.DeleteValue("AccessVBOM");
                        vbomKey.Close();
                    } else
                    {
                        WriteError("Something went wrong. We created the key previously, but it was missing when trying to delete it.");
                    }
                }
            }

        }

        private void CreateRegistryKey(string application)
        {
            for (int i = 7; i <= 16; i++)
            {
                string version = i.ToString() + ".0";
                RegistryKey vbomKey = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\Office\{version}\{application}\Security", true);
                if (vbomKey != null)
                {
                    string keyPath = vbomKey.ToString() + @"\AccessVBOM";
                    WriteWarning($"Creating {keyPath} and setting the value to 1");
                    vbomKey.SetValue("AccessVBOM", "1", Microsoft.Win32.RegistryValueKind.DWord);
                    vbomKey.Close();
                    
                }
            }
        }

        // Searches for blocking keys in HKCU and switches them if it's found
        // Returns true if there is a key that blocks us
        // Input is the office application. Currently 'Word|Excel'.
        private int CheckHKCURegistryBlock(string application)
        {
            // In testing, if the AccessVBOM DWORD value doesn't exist or if it's set to 0, then this enumeration is blocked
            // If the DWORD value does exist and it's set to 1, then this enumeration is allowed

            // We track if we found the Security key or not to choose the right return value
            bool keyFound = false;

            // Version reg keys found from Stack Overflow: https://stackoverflow.com/questions/3266675/how-to-detect-installed-version-of-ms-office
            // Iterate over all possible versions looking for our security key called AccessVBOM
            for (int i = 7; i <= 16; i++)
            {
                string version = i.ToString() + ".0";
                RegistryKey vbomKey = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\Office\{version}\{application}\Security", true);
                if (vbomKey != null)
                {
                    keyFound = true;
                    if (vbomKey.GetValueNames().Contains("AccessVBOM"))
                    {
                        if (vbomKey.GetValue("AccessVBOM").ToString() != "1")
                        {
                            // Change the DWORD value to allow the enumeration
                            SetRegistryKey(application, "1");
                            // The DWORD value was set to something other than Allow, so break out early and notify that it's blocked
                            return (int) RegistryBlock.BLOCKED;
                        }
                    } else 
                    {
                        // create key here
                        CreateRegistryKey(application);
                        return (int) RegistryBlock.MISSING_DWORD;
                    }
                }
            }
            // We got to the end and either didn't find the reg key at all or didn't find a blocker
            if (keyFound)
            {
                // We found at least one key with the DWORD value there, so the software seems to be installed, 
                // but didn't break so we didn't find any blocks
                return (int) RegistryBlock.UNBLOCKED;
            }
            else
            {
                // We never even found a key for this
                // It's likely that the application isn't installed
                return (int)RegistryBlock.MISSING_KEY;
            }
        }

        private bool CheckHKLMRegistryBlock(string application)
        {
            // Version reg keys found from Stack Overflow: https://stackoverflow.com/questions/3266675/how-to-detect-installed-version-of-ms-office
            // Iterate over all possible versions looking for our security key called AccessVBOM
            for (int i = 7; i <= 16; i++)
            {
                string version = i.ToString() + ".0";
                RegistryKey vbomKey = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Office\{version}\{application}\Security", true);
                if (vbomKey != null)
                {
                    if (vbomKey.GetValueNames().Contains("AccessVBOM"))
                    {
                        if (vbomKey.GetValue("AccessVBOM").ToString() == "1")
                        {
                            // The key value is set to allow
                            return false;
                        } else if (vbomKey.GetValue("AccessVBOM").ToString() == "0")
                        {
                            // The key value is set to block
                            return true;
                        }
                    } else
                    {
                        // DWORD wasn't there, so we need to create it
                    }
                }
            }
            // Key didn't exist at all. It's likely office isn't even installed. Take no actions
            return false;

        }

        private List<MacroModule> GetExcelMacros(string fileName)
        {

            Excel.Application excelApp = new Excel.Application();
            excelApp.Visible = false;
            object missing = System.Reflection.Missing.Value;
            object readOnly = true;
            Excel.Workbook book = excelApp.Workbooks.Open(Filename: fileName, ReadOnly: readOnly);
            //List<MacroModule> lstMacros = new List<MacroModule>();
            List<MacroModule> macros = new List<MacroModule>();

            VBA.VBProject prj;
            VBA.CodeModule code;
            string composedFile;
            MacroModule module;

            prj = book.VBProject;
            foreach (VBA.VBComponent comp in prj.VBComponents)
            {
                module = new MacroModule();

                code = comp.CodeModule;
                composedFile = "    ";

                // Put the name of the code module at the top
                //composedFile = comp.Name + Environment.NewLine;
                module.Name = comp.Name;

                // Loop through the (1-indexed) lines
                for (int i = 0; i < code.CountOfLines; i++)
                {
                    composedFile += "    " + code.get_Lines(i + 1, 1) + Environment.NewLine;
                }

                module.Code = composedFile;
                // Add the macro to the list
                macros.Add(module);
            }


            book.Close(0);
            excelApp.Quit();
            return macros;
        }

        static List<MacroModule> GetWordMacros(string fileName)
        {
            //string macros = "";
            Word.Application wordApp = new Word.Application();
            wordApp.Visible = false;
            object missing = System.Reflection.Missing.Value;
            object readOnly = true;
            Word.Document doc = wordApp.Documents.Open(fileName, ref missing, ref readOnly, ref missing, ref missing, ref missing, ref missing, ref missing, ref missing, ref missing, ref missing, ref missing, ref missing, ref missing, ref missing, ref missing);
            List<MacroModule> macros = new List<MacroModule>();

            VBA.VBProject prj;
            VBA.CodeModule code;
            string composedFile;
            MacroModule module;

            prj = doc.VBProject;
            foreach (VBA.VBComponent comp in prj.VBComponents)
            {
                module = new MacroModule();

                code = comp.CodeModule;
                composedFile = "    ";

                // Put the name of the code module at the top
                //composedFile = comp.Name + Environment.NewLine;
                module.Name = comp.Name;

                // Loop through the (1-indexed) lines
                for (int i = 0; i < code.CountOfLines; i++)
                {
                    composedFile += "    " + code.get_Lines(i + 1, 1) + Environment.NewLine;
                }

                module.Code = composedFile;
                // Add the macro to the list
                macros.Add(module);
            }

            
            doc.Close();
            wordApp.Quit();
            return macros;

        }

        public override IEnumerable<CommandDTOBase?> Execute(string[] args)
        {
            var lastDays = 7;
            bool lastDaysChanged = false;
            bool checkForMacros = false;

            // We want to track what changes we make to the system so that we can revert those changes
            int excelKeyChanged = (int) RegistryBlock.UNBLOCKED;
            int wordKeyChanged = (int) RegistryBlock.UNBLOCKED;

            // parses recent file shortcuts via COM

            if (args.Length == 1 && !args.Contains("/checkForMacros"))
            {
                // Since we only received 1 argument and it's not /checkForMacros, assume it's the lastDays argument and attempt to parse it as an int
                lastDays = int.Parse(args[0]);
                lastDaysChanged = true;
            }
            else if (args.Length == 2 && args.Contains("/checkForMacros"))
            {
                // We know one is /checkForMacros, so try to parse the other as an int
                int indexMacros = Array.IndexOf(args, "/checkForMacros");

                if (indexMacros == 0)
                {
                    if (!int.TryParse(args[1], out lastDays))
                    {
                        throw new ArgumentException("lastDays is not an integer");
                    } else
                    {
                        lastDaysChanged = true;
                    }
                }
                else
                {
                    if (!int.TryParse(args[0], out lastDays))
                    {
                        throw new ArgumentException("lastDays is not an integer");
                    } else
                    {
                        lastDaysChanged = true;
                    }

                }

                wordKeyChanged = CheckHKCURegistryBlock("Word");
                excelKeyChanged = CheckHKCURegistryBlock("Excel");
                if (CheckHKLMRegistryBlock("Word") || CheckHKLMRegistryBlock("Excel"))
                {
                    WriteWarning("The /checkForMacros flag has been provided but one or more registry keys in HKLM blocks this, so the checkForMacros will not be performed.");
                    WriteWarning("If you have admin, you can manually set these values then run again");

                }
                else if ((int) wordKeyChanged > 1 || (int) excelKeyChanged > 1)
                {
                    WriteWarning("The /checkForMacros flag has been provided but one or more registry keys in HKCU blocks this. Those keys will be temporarily modified or created to allow this enumeration.");
                    checkForMacros = true;
                }
                else
                {
                    // We didn't find the block, so continue with the macro check
                    checkForMacros = true;
                }

            } else if (args.Length == 1 && args.Contains("/checkForMacros")) {
                // We have 1 argument and it's /checkForMacros

                wordKeyChanged = CheckHKCURegistryBlock("Word");
                excelKeyChanged = CheckHKCURegistryBlock("Excel");
                if (CheckHKLMRegistryBlock("Word") || CheckHKLMRegistryBlock("Excel"))
                {
                    WriteWarning("The /checkForMacros flag has been provided but one or more registry keys in HKLM blocks this, so the checkForMacros will not be performed.");
                    WriteWarning("If you have admin, you can manually set these values then run again");

                }
                else if ((int)wordKeyChanged > 1 || (int)excelKeyChanged > 1)
                {
                    WriteWarning("The /checkForMacros flag has been provided but one or more registry keys in HKCU blocks this. Those keys will be temporarily modified or created to allow this enumeration.");
                    checkForMacros = true;
                } else
                {
                    // We didn't find the block, so continue with the macro check
                    checkForMacros = true;
                }

            } else if (args.Length >= 2)
            {
                // We have hit two or more args and one of them isn't /checkForMacros. This isn't right.
                throw new ArgumentException("An incorrect argument was detected");
            }


            // !Runtime.FilterResults == -full
            // The operator selected -full and didn't overwrite lastDays
            if (!Runtime.FilterResults && !lastDaysChanged)
            {
                lastDays = 30;
            }

            WriteHost("Enumerating Office most recently used files for the last {0} days", lastDays);
            WriteHost("\n  {0,-8}  {1,-23}  {2,-12}  {3}", "App", "User", "LastAccess", "FileName");
            WriteHost("  {0,-8}  {1,-23}  {2,-12}  {3}", "---", "----", "----------", "--------");

            foreach (var file in EnumRecentOfficeFiles(lastDays, checkForMacros).OrderByDescending(e => ((OfficeRecentFilesDTO)e).LastAccessDate))
            {
                yield return file;
            }

            if (excelKeyChanged == (int) RegistryBlock.BLOCKED)
            {
                // In testing, a small sleep was needed to change the registry key back. The COM object might have been holding the key, preventing us from changing it.
                Thread.Sleep(500);
                SetRegistryKey("Excel", "0");
            } else if (excelKeyChanged == (int)RegistryBlock.MISSING_DWORD)
            {
                Thread.Sleep(500);
                DeleteRegistryKey("Excel");
            }
            
            if (wordKeyChanged == (int)RegistryBlock.BLOCKED)
            {
                Thread.Sleep(500);
                SetRegistryKey("Word", "0");
            } else if (wordKeyChanged == (int)RegistryBlock.MISSING_DWORD)
            {
                Thread.Sleep(500);
                DeleteRegistryKey("Word");
            }

        }

        private IEnumerable<CommandDTOBase> EnumRecentOfficeFiles(int lastDays, bool checkForMacros)
        {

            // I currently don't know if docb and xlsb can be read with this method. need additional testing
            string[] wordExtensions = { ".doc", ".dot", ".dotx", ".dotm", ".docm", ".docb" };
            string[] excelExtensions = { ".xls", ".xlt", ".xltx", ".xltm", ".xlsm", ".xlsb" };

            foreach (var sid in Registry.Users.GetSubKeyNames())
            {
                if (!sid.StartsWith("S-1") || sid.EndsWith("_Classes"))
                {
                    continue;
                }

                string userName = null;
                try
                {
                    userName = Advapi32.TranslateSid(sid);
                }
                catch
                {
                    userName = sid;
                }
                
                var officeVersion = 
                    RegistryUtil.GetSubkeyNames(RegistryHive.Users, $"{sid}\\Software\\Microsoft\\Office")
                    ?.Where(k => float.TryParse(k, NumberStyles.AllowDecimalPoint, new CultureInfo("en-GB"), out _));

                if (officeVersion is null)
                    continue;

                foreach (var version in officeVersion)
                {
                    foreach (OfficeRecentFilesDTO mru in GetMRUsFromVersionKey($"{sid}\\Software\\Microsoft\\Office\\{version}"))
                    {
                        if (mru.LastAccessDate <= DateTime.Now.AddDays(-lastDays)) continue;

                        if (checkForMacros)
                        {
                            string extension = Path.GetExtension(mru.Target);
                            try
                            {
                                if (wordExtensions.Contains(extension))
                                {
                                    WriteHost($"Checking for Word macros in {mru.Target}");
                                    mru.Macros = GetWordMacros(mru.Target);
                                }
                                else if (excelExtensions.Contains(extension))
                                {
                                    WriteHost($"Checking for Excel macros in {mru.Target}");
                                    mru.Macros = GetExcelMacros(mru.Target);
                                }
                                else
                                {
                                    //WriteWarning($"Extension '{extension}' unsupported");
                                }
                            }
                            catch (Exception)
                            {
                                WriteError($"Hit an issue trying to read {mru.Target}.");
                                continue;
                            }
                        }

                        mru.User = userName;
                        yield return mru;
                    }
                }
            }
        }

        private IEnumerable<CommandDTOBase> GetMRUsFromVersionKey(string officeVersionSubkeyPath)
        {
            var officeApplications = RegistryUtil.GetSubkeyNames(RegistryHive.Users, officeVersionSubkeyPath);
            if (officeApplications == null)
            {
                yield break;
            }

            foreach (var app in officeApplications)
            {
                // 1) HKEY_CURRENT_USER\Software\Microsoft\Office\16.0\<OFFICE APP>\File MRU
                foreach (var mru in GetMRUsValues($"{officeVersionSubkeyPath}\\{app}\\File MRU"))
                {
                    yield return mru;
                }

                // 2) HKEY_CURRENT_USER\Software\Microsoft\Office\16.0\Word\User MRU\ADAL_B7C22499E768F03875FA6C268E771D1493149B23934326A96F6CDFEEEE7F68DA72\File MRU
                // or HKEY_CURRENT_USER\Software\Microsoft\Office\16.0\Word\User MRU\LiveId_CC4B824314B318B42E93BE93C46A61575D25608BBACDEEEA1D2919BCC2CF51FF\File MRU

                var logonAapps = RegistryUtil.GetSubkeyNames(RegistryHive.Users, $"{officeVersionSubkeyPath}\\{app}\\User MRU");
                if (logonAapps == null)
                    continue;

                foreach (var logonApp in logonAapps)
                {
                    foreach (var mru in GetMRUsValues($"{officeVersionSubkeyPath}\\{app}\\User MRU\\{logonApp}\\File MRU"))
                    {
                        ((OfficeRecentFilesDTO)mru).Application = app;
                        yield return mru;
                    }
                }
            }
        }

        private IEnumerable<CommandDTOBase> GetMRUsValues(string fileMRUKeyPath)
        {
            var values = RegistryUtil.GetValues(RegistryHive.Users, fileMRUKeyPath);
            if (values == null) yield break;
            foreach (string mru in values.Values)
            {
                var m = ParseMruString(mru);
                if (m != null)
                {
                    yield return m;
                }
            }
        }


        private OfficeRecentFilesDTO? ParseMruString(string mru)
        {
            var matches = Regex.Matches(mru, "\\[[a-zA-Z0-9]+?\\]\\[T([a-zA-Z0-9]+?)\\](\\[[a-zA-Z0-9]+?\\])?\\*(.+)");
            if (matches.Count == 0)
            {
                return null;
            }

            long timestamp = 0;
            var dateHexString = matches[0].Groups[1].Value;
            var filename = matches[0].Groups[matches[0].Groups.Count - 1].Value;

            try
            {
                timestamp = long.Parse(dateHexString, NumberStyles.HexNumber);
            }
            catch
            {
                WriteError($"Could not parse MRU timestamp. Parsed timestamp: {dateHexString} MRU value: {mru}");
            }

            return new OfficeRecentFilesDTO()
            {
                Application = "Office",
                LastAccessDate = DateTime.FromFileTimeUtc(timestamp),
                User = null,
                Target = filename
            };
        }

        internal class OfficeRecentFilesDTO : CommandDTOBase
        {
            public string Application { get; set; }
            public string Target { get; set; }
            public DateTime LastAccessDate { get; set; }
            public string User { get; set; }
            public List<MacroModule> Macros { get; set; }
        }

        [CommandOutputType(typeof(OfficeRecentFilesDTO))]
        internal class OfficeMRUsCommandFormatter : TextFormatterBase
        {
            public OfficeMRUsCommandFormatter(ITextWriter writer) : base(writer)
            {
            }

            public override void FormatResult(CommandBase? command, CommandDTOBase result, bool filterResults)
            {
                var dto = (OfficeRecentFilesDTO)result;

                WriteLine("  {0,-8}  {1,-23}  {2,-12}  {3}", dto.Application, dto.User, dto.LastAccessDate.ToString("yyyy-MM-dd"), dto.Target);


                if (dto.Macros!= null)
                {
                    foreach(MacroModule macro in dto.Macros) {
                        if (!macro.Code.Trim().Equals(""))
                        {
                            WriteLine("    Macro Module: {0}", macro.Name);
                            WriteLine("    Macro Code:   {0}", macro.Code);
                            WriteLine("  -----------------------");
                            WriteLine("");
                        }
                    }
                }
            }
        }
    }
}
#nullable enable
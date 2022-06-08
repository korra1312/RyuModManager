using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using IniParser;
using IniParser.Model;
using Octokit;
using Utils;
using RyuCLI.Templates;
using ParRepacker;

using static ModLoadOrder.Generator;
using static Utils.GamePath;
using static Utils.Constants;

namespace RyuCLI
{
    public static class Program
    {
        private const string VERSION = "v1.7";
        private const string AUTHOR = "SutandoTsukai181";
        private const string REPO = "RyuModManager";

        public static async Task Main(string[] args)
        {
            bool externalModsOnly = true;
            bool looseFilesEnabled = false;
            bool checkForUpdates = true;
            bool isSilent = false;

            var iniParser = new FileIniDataParser();
            iniParser.Parser.Configuration.AssigmentSpacer = string.Empty;

            Console.WriteLine($"Ryu Mod Manager CLI {VERSION}");
            Console.WriteLine($"By {AUTHOR}\n");

            if (args.Length == 0)
            {
                Console.WriteLine($"No arguments were passed. Will generate Mod Load Order and repack pars...\n");
            }
            else
            {
                List<string> list = new List<string>(args);
                if (list.Contains("-s") || list.Contains("--silent"))
                {
                    isSilent = true;
                }
            }

            if (File.Exists(INI))
            {
                IniData ini = iniParser.ReadFile(INI);

                if (ini.TryGetKey("Overrides.LooseFilesEnabled", out string looseFiles))
                {
                    looseFilesEnabled = int.Parse(looseFiles) == 1;
                }

                if (ini.TryGetKey("RyuModManager.Verbose", out string verbose))
                {
                    ConsoleOutput.Verbose = int.Parse(verbose) == 1;
                }

                if (ini.TryGetKey("RyuModManager.CheckForUpdates", out string check))
                {
                    checkForUpdates = int.Parse(check) == 1;
                }

                if (ini.TryGetKey("RyuModManager.ShowWarnings", out string showWarnings))
                {
                    ConsoleOutput.ShowWarnings = int.Parse(showWarnings) == 1;
                }

                if (ini.TryGetKey("RyuModManager.LoadExternalModsOnly", out string extMods))
                {
                    externalModsOnly = int.Parse(extMods) == 1;
                }

                if (!ini.TryGetKey("Parless.IniVersion", out string iniVersion) || int.Parse(iniVersion) < ParlessIni.CurrentVersion)
                {
                    // Update if ini version is old (or does not exist)
                    Console.Write(INI + " is outdated. Updating ini to the latest version... ");
                    iniParser.WriteFile(INI, IniTemplate.UpdateIni(ini));
                    Console.WriteLine("DONE!\n");
                }
            }
            else
            {
                // Create ini if it does not exist
                Console.Write(INI + " was not found. Creating default ini... ");
                iniParser.WriteFile(INI, IniTemplate.NewIni());
                Console.WriteLine("DONE!\n");
            }

            if (isSilent)
            {
                // No need to check if console won't be shown anyway
                checkForUpdates = false;
            }

            Task<ConsoleOutput> updateCheck = null;

            if (checkForUpdates)
            {
                // Start checking for updates before the actual generation is done
                updateCheck = Task.Run(() => CheckForUpdates());
            }

            List<string> mods = new List<string>();

            if (externalModsOnly && Directory.Exists(GetExternalModsPath()))
            {
                // Only load the files inside the external mods path, and ignore the load order in the txt
                mods.Add(EXTERNAL_MODS);
            }
            else if (File.Exists(TXT))
            {
                StreamReader file = new StreamReader(new FileInfo(TXT).FullName);

                string line;
                while ((line = file.ReadLine()) != null)
                {
                    if (!line.StartsWith(';'))
                    {
                        line = line.Split(';', 1)[0];

                        // Only add existing mods that are not duplicates
                        if (line.Length > 0 && Directory.Exists(Path.Combine(MODS, line)) && !mods.Contains(line))
                        {
                            mods.Add(line);
                        }
                    }
                }

                file.Close();
            }
            else
            {
                // Create txt if it does not exist
                Console.Write(TXT + " was not found. Creating empty txt... ");
                File.WriteAllText(TXT, TxtTemplate.TxtContent);
                Console.WriteLine("DONE!\n");
            }

            if (GamePath.GetGame() != Game.Unsupported)
            {
                if (!Directory.Exists(MODS))
                {
                    // Create mods folder if it does not exist
                    Console.Write($"\"{MODS}\" folder was not found. Creating empty folder... ");
                    Directory.CreateDirectory(MODS);
                    Console.WriteLine("DONE!\n");
                }

                if (File.Exists(MLO))
                {
                    Console.Write("Removing old MLO...");

                    // Remove existing MLO file to avoid it being used if a new MLO won't be generated
                    File.Delete(MLO);

                    Console.WriteLine(" DONE!\n");
                }

                // Remove previously repacked pars, to avoid unwanted side effects
                Repacker.RemoveOldRepackedPars();

                if (mods.Count > 0 || looseFilesEnabled)
                {
                    await GenerateModLoadOrder(mods, looseFilesEnabled).ConfigureAwait(false);
                }
                else
                {
                    Console.WriteLine("Aborting: No mods were found, and .parless paths are disabled\n");
                }
            }
            else
            {
                Console.WriteLine("Aborting: No supported game was found in this directory\n");
            }

            // TODO: Maybe move this to a separate "Game patches" file
            // Virtua Fighter eSports crashes when used with dinput8.dll as the ASI loader
            if (GamePath.GetGame() == Game.eve && File.Exists(DINPUT8DLL))
            {
                if (File.Exists(VERSIONDLL))
                {
                    Console.Write($"Game specific patch: Deleting {DINPUT8DLL} because {VERSIONDLL} exists...");

                    // Remove dinput8.dll
                    File.Delete(DINPUT8DLL);
                }
                else
                {
                    Console.Write($"Game specific patch: Renaming {DINPUT8DLL} to {VERSIONDLL}...");

                    // Rename dinput8.dll to version.dll to prevent the game from crashing
                    File.Move(DINPUT8DLL, VERSIONDLL);
                }

                Console.WriteLine(" DONE!\n");
            }

            // Check if the ASI loader is not in the directory (possibly due to incorrect zip extraction)
            if (!(File.Exists(DINPUT8DLL) || File.Exists(VERSIONDLL)))
            {
                Console.WriteLine($"Warning: \"{DINPUT8DLL}\" is missing from this directory. RyuModManager will NOT function properly without this file\n");
            }

            // Check if the ASI is not in the directory
            if (!File.Exists(ASI))
            {
                Console.WriteLine($"Warning: \"{ASI}\" is missing from this directory. RyuModManager will NOT function properly without this file\n");
            }

            // Calculate the checksum for the game's exe to inform the user if their version might be unsupported
            if (ConsoleOutput.ShowWarnings && GetGame() != Game.Unsupported && !GameHash.ValidateFile(Path.Combine(GetGamePath(), GetGameExe()), GetGame()))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Warning: Game version is unsupported. Please use the latest Steam version of the game.");
                Console.WriteLine($"RyuModManager will still generate the load order, but the game might CRASH or not function properly\n");
                Console.ResetColor();
            }

            if (checkForUpdates)
            {
                Console.WriteLine("Checking for updates...");

                // Wait for a maximum of 5 seconds for the update check if it was not finished
                updateCheck.Wait(5000);
                var updateConsole = await updateCheck.ConfigureAwait(false);

                if (updateConsole != null)
                {
                    updateConsole.Flush();
                }
                else
                {
                    Console.WriteLine("Unable to check for updates\n");
                }
            }

            if (!isSilent)
            {
                Console.WriteLine("Program finished. Press any key to exit...");
                Console.ReadKey();
            }
        }

        private static async Task<ConsoleOutput> CheckForUpdates()
        {
            try
            {
                ConsoleOutput console = new ConsoleOutput();
                var client = new GitHubClient(new ProductHeaderValue(REPO));
                var latestRelease = await client.Repository.Release.GetLatest(AUTHOR, REPO).ConfigureAwait(false);

                if (latestRelease != null && latestRelease.Name.Contains("CLI") && latestRelease.TagName != VERSION)
                {
                    console.WriteLine("New version detected!\n");
                    console.WriteLine($"Current version: {VERSION}");
                    console.WriteLine($"New version: {latestRelease.TagName}\n");

                    console.WriteLine($"Please update by going to {latestRelease.HtmlUrl}");
                }
                else
                {
                    console.WriteLine("Current version is up to date");
                }

                return console;
            }
            catch
            {

            }

            return null;
        }
    }
}

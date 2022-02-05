﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Assets.Scripts.Unity.UI_New.Popups;
using BTD_Mod_Helper.Api.ModMenu;
using MelonLoader;
using Newtonsoft.Json;
using Octokit;

namespace BTD_Mod_Helper.Api
{
    internal static class ModHelperGithub
    {
        public const string RawUserContent = "https://raw.githubusercontent.com";
        
        private const string Topic = "btd6-mod";
        private const string ProductName = "btd-mod-helper";
        private const string VerifiedModdersURL = "";
        
        private const string DllContentType = "application/x-msdownload";
        private const string ZipContentType = "application/zip";
        private const string ZipContentType2 = "application/x-zip-compressed";

        private const string Sorry =
            "Please try again at a later time, and if it still doesn't work, contact the mod developer.";

        public static List<ModHelperData> Mods { get; private set; } = new List<ModHelperData>();

        // Will be moved to checking from a json file within the mod helper repo
        public static readonly HashSet<string> VerifiedModders = new HashSet<string>
        {
            "doombubbles",
            "gurrenm3"
        };

        public static GitHubClient Client { get; private set; }

        private static MiscellaneousRateLimit rateLimit;

        public static int RemainingSearches => rateLimit?.Resources?.Search?.Remaining ?? -1;

        public static void Init()
        {
            Client = new GitHubClient(new ProductHeaderValue(ProductName));
        }

        public static async Task PopulateMods()
        {
            var searchRepositoryResult =
                await Client.Search.SearchRepo(new SearchRepositoriesRequest($"topic:{Topic}"));


            var mods = searchRepositoryResult.Items
                .OrderBy(repo => repo.CreatedAt)
                .Select(repo => new ModHelperData(repo))
                .ToArray();

            ModHelper.Msg("finished getting mods");

            Task.WhenAll(mods.Select(data => data.LoadDataFromRepoAsync())).Wait();

            Mods = mods.Where(mod => mod.RepoDataSuccess).ToList();
            
            UpdateRateLimit();
        }

        public static async Task GetVerifiedModders()
        {
            try
            {
                var result = await ModHelperHttp.Client.GetStringAsync(VerifiedModdersURL);
                var strings = JsonConvert.DeserializeObject<string[]>(result);
                if (strings != null)
                {
                    foreach (var s in strings)
                    {
                        ModHelper.Msg($"Found verified modder {s}");
                        VerifiedModders.Add(s);
                    }
                }
            }
            catch (Exception e)
            {
                ModHelper.Warning("Failed to get verified modders list");
                ModHelper.Warning(e);
            }
        }

        public static async Task DownloadLatest(ModHelperData mod, bool bypassPopup = false,
            Action<string> callback = null)
        {
            var latestRelease = mod.LatestRelease ?? await mod.GetLatestRelease();
            if (latestRelease == null)
            {
                PopupScreen.instance.ShowOkPopup($"Failed to get latest release from the GitHub API. {Sorry}");
                return;
            }

            var action = new Action(() =>
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var resultFile = await DownloadRelease(mod, latestRelease);
                        if (resultFile != null)
                        {
                            if (callback != null && !string.IsNullOrWhiteSpace(resultFile))
                            {
                                callback(resultFile);
                            }

                            return;
                        }
                    }
                    catch (Exception e)
                    {
                        ModHelper.Warning(e);
                    }

                    PopupScreen.instance.ShowOkPopup($"Failed to download asset. {Sorry}");
                });
            });

            if (bypassPopup)
            {
                action.Invoke();
            }
            else
            {
                PopupScreen.instance.ShowPopup(PopupScreen.Placement.menuCenter,
                    $"Do you want to download\n{mod.Name} v{mod.RepoVersion}?",
                    "Latest Release Message:\n\"" + latestRelease.Body + "\"",
                    action, "Yes", null, "No", Popup.TransitionAnim.Scale);
            }

            UpdateRateLimit();
        }

        public static async Task<string> DownloadRelease(ModHelperData mod, Release release)
        {
            try
            {
                var releaseAsset = release.Assets.FirstOrDefault(asset => asset.Name == mod.DllName) ??
                                   release.Assets.First();

                if (mod.ManualDownload)
                {
                    Process.Start(releaseAsset.BrowserDownloadUrl);
                    return "";
                }

                return await DownloadAsset(mod, releaseAsset);
            }
            catch (Exception e)
            {
                ModHelper.Warning(e);
                return null;
            }
        }

        public static async Task<string> DownloadAsset(ModHelperData mod, ReleaseAsset releaseAsset)
        {
            var name = mod.DllName ?? releaseAsset.Name;
            if (name == null || !name.EndsWith(".dll"))
            {
                name = $"{mod.Mod.Assembly.GetName().Name}.dll";
            }

            var downloadFilePath = Path.Combine(MelonHandler.ModsDirectory, name);
            var oldModsFilePath = Path.Combine(ModHelper.OldModsDirectory, name);

            try
            {
                if (mod.FilePath != null && File.Exists(mod.FilePath))
                {
                    if (!Directory.Exists(ModHelper.OldModsDirectory))
                    {
                        Directory.CreateDirectory(ModHelper.OldModsDirectory);
                    }

                    File.Copy(mod.FilePath, oldModsFilePath, true);
                    ModHelper.Msg($"Backing up to {oldModsFilePath}");
                }

                var success = false;
                switch (releaseAsset.ContentType)
                {
                    default:
                        throw new ArgumentException(
                            $"Won't download release asset with content type {releaseAsset.ContentType}");
                    case DllContentType:
                        success = await ModHelperHttp.DownloadFile(releaseAsset.BrowserDownloadUrl, downloadFilePath);
                        break;
                    case ZipContentType:
                    case ZipContentType2:
                        var zippedFiles = await ModHelperHttp.DownloadZip(releaseAsset.BrowserDownloadUrl);
                        if (zippedFiles != null)
                        {
                            try
                            {
                                var dll = zippedFiles.First(s => s.EndsWith(name));
                                File.Copy(dll, downloadFilePath, true);
                                success = true;
                            }
                            catch (InvalidOperationException)
                            {
                                ModHelper.Warning(
                                    $"Zip archive did not contain {name}. " +
                                    "The mod developer may have made a typo, " +
                                    "or needs to use the DllName property in their ModHelperData.");
                            }
                        }

                        break;
                }

                if (success)
                {
                    PopupScreen.instance.ShowOkPopup(
                        $"Successfully downloaded {name}\nRemember to restart to apply the changes!");
                    mod.SetVersion(mod.RepoVersion);
                    return downloadFilePath;
                }
            }
            catch (Exception e)
            {
                ModHelper.Warning(e);
            }

            if (File.Exists(oldModsFilePath))
            {
                File.Copy(oldModsFilePath, downloadFilePath, true);
                ModHelper.Msg($"Loading backup from {oldModsFilePath}");
            }

            return null;
        }


        public static void UpdateRateLimit()
        {
            Task.Run(async () =>
            {
                try
                {
                    rateLimit = await Client.Miscellaneous.GetRateLimits();
                }
                catch (Exception e)
                {
                    ModHelper.Warning(e);
                }
            });
        }
    }
}
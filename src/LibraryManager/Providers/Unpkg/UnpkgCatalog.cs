﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.LibraryManager.Contracts;
using Newtonsoft.Json.Linq;

namespace Microsoft.Web.LibraryManager.Providers.Unpkg
{
    internal class UnpkgCatalog : ILibraryCatalog
    {
        public const string CacheFileName = "cache.json";
        public const string LibraryFileListUrlFormat = "http://unpkg.com/{0}/?meta";
        public const string LatestLibraryVersonUrl = "http://unpkg.com/{0}/package.json"; 
        private UnpkgProvider _provider;
        private CacheService _cacheService;
        private string _cacheFile;


        public UnpkgCatalog(UnpkgProvider provider)
        {
            _provider = provider;
            // TODO: {alexgav} Do we need multiple instances of this?
            _cacheService = new CacheService(WebRequestHandler.Instance);
            _cacheFile = Path.Combine(provider.CacheFolder, CacheFileName);
        }

        public async Task<string> GetLatestVersion(string libraryId, bool includePreReleases, CancellationToken cancellationToken)
        {
            string latestVersion = null;

            try
            {
                ILibrary library = await GetLibraryAsync(libraryId, cancellationToken).ConfigureAwait(false);

                if (library != null)
                {
                    string name = library.Name;
                    string latestLibraryVersionUrl = string.Format(LatestLibraryVersonUrl, name);

                    JObject packageObject = await WebRequestHandler.Instance.GetJsonObjectViaGetAsync(latestLibraryVersionUrl, cancellationToken);

                    if (packageObject != null)
                    {
                        JValue versionValue = packageObject["version"] as JValue;
                        latestVersion = versionValue?.Value as string;
                    }
                }
            }
            catch(Exception ex)
            {
                _provider.HostInteraction.Logger.Log(ex.ToString(), LogLevel.Error);
            }

            return latestVersion;
        }

        public async Task<ILibrary> GetLibraryAsync(string libraryId, CancellationToken cancellationToken)
        {
            ILibrary library = await GetLibraryAsync(libraryId, cancellationToken).ConfigureAwait(false);

            if (library != null)
            {
                string name = library.Name;
                string version = library.Version;

                IEnumerable<string> libraryFiles = await GetLibraryFilesAsync(libraryId, cancellationToken);

                return new UnpkgLibrary
                {
                    Version = version,
                    Files = libraryFiles.ToDictionary(k => k, b => false),
                    Name = name,
                    ProviderId = _provider.Id,
                };
            }

            return null;
        }

        private async Task<IEnumerable<string>> GetLibraryFilesAsync(string libraryId, CancellationToken cancellationToken)
        {
            List<string> result = new List<string>();

            try
            {
                string libraryFileListUrl = string.Format(LibraryFileListUrlFormat, libraryId);
                JObject fileListObject = await WebRequestHandler.Instance.GetJsonObjectViaGetAsync(libraryFileListUrl, cancellationToken).ConfigureAwait(false);


                if (fileListObject != null)
                {
                    GetFiles(fileListObject, result);
                }
            }
            catch (Exception ex)
            {
                _provider.HostInteraction.Logger.Log(ex.ToString(), LogLevel.Error);
            }

            return result;
        }

        private void GetFiles(JObject fileObject, List<string> files)
        {
            // Parse JSON document returned by unpkg.com/libraryname@version/?meta
            // It looks something like
            // { 
            //   "type" : "directory",
            //   "path" : "/"
            //   "files" : [
            //     {
            //       "type" : "directory"
            //       "path" : "/umd"
            //       "files" : [
            //         {
            //           "type" : "file",
            //           "path" : "/umd/react.development.js"
            //         },
            //         {
            //           "type" : "file",
            //           "path" : "/umd/react.production.min.js"
            //         }
            //       ]
            //     },
            //     {
            //       "type" : "file",
            //       "path" : "/index.js"
            //     }
            //   ]
            // }

            if (files == null)
            {
                throw new ArgumentNullException(nameof(files));
            }

            if (fileObject != null)
            {
                JValue type = fileObject["type"] as JValue; // will be either "file" or "directory"
                if (type.Value as string == "file")
                {
                    JValue pathValue = fileObject["path"] as JValue;
                    string path = pathValue?.Value as string;

                    if (path != null && path.Length > 0)
                    {
                        // Don't include the leading "/" in the file paths, do you get dist/jquery.js rather than /dist/jquery.js
                        // We will want the user to always specify a relative path in the "Files" array of the library entry
                        files.Add(path.Substring(1));
                    }
                }
                else if (type.Value as string == "directory")
                {
                    JArray filesArray = fileObject["files"] as JArray;
                    if (filesArray != null)
                    {
                        foreach (JObject childFileObject in filesArray)
                        {
                            GetFiles(childFileObject, files);
                        }
                    }
                }
            }
        }

        public async Task<CompletionSet> GetLibraryCompletionSetAsync(string libraryNameStart, int caretPosition)
        {
            CompletionSet completionSet = new CompletionSet
            {
                Start = 0,
                Length = libraryNameStart.Length
            };

            List<CompletionItem> completions = new List<CompletionItem>();
            
            try
            {
                ILibrary library = await GetLibraryAsync(libraryNameStart, CancellationToken.None).ConfigureAwait(false);

                if (library != null)
                {
                    string name = library.Name;
                    string version = library.Version;

                    // library name completion
                    if (caretPosition < name.Length + 1)
                    {
                        IEnumerable<string> packageNames = await NpmPackageSearch.GetPackageNamesAsync(libraryNameStart, CancellationToken.None);

                        foreach (string packageName in packageNames)
                        {
                            CompletionItem completionItem = new CompletionItem
                            {
                                DisplayText = packageName,
                                InsertionText = packageName
                            };

                            completions.Add(completionItem);
                        }
                    }

                    // library version completion
                    else
                    {
                        completionSet.Start = name.Length + 1;
                        completionSet.Length = version.Length;

                        NpmPackageInfo npmPackageInfo = await NpmPackageInfoCache.GetPackageInfoAsync(name, CancellationToken.None);
                        foreach (SemanticVersion semVersion in npmPackageInfo.Versions)
                        {
                            string itemText = name + "@" + semVersion.ToString();
                            CompletionItem completionItem = new CompletionItem
                            {
                                DisplayText = itemText,
                                InsertionText = itemText
                            };

                            completions.Add(completionItem);
                        }
                    }
                }

                completionSet.Completions = completions;
            }
            catch (Exception ex)
            {
                _provider.HostInteraction.Logger.Log(ex.ToString(), LogLevel.Error);
            }

            return completionSet;
        }

        public async Task<IReadOnlyList<ILibraryGroup>> SearchAsync(string term, int maxHits, CancellationToken cancellationToken)
        {
            List<ILibraryGroup> libraryGroups = new List<ILibraryGroup>();

            try
            {
                IEnumerable<string> packageNames = await NpmPackageSearch.GetPackageNamesAsync(term, CancellationToken.None);
                libraryGroups = packageNames.Select(packageName => new UnpkgLibraryGroup(packageName)).ToList<ILibraryGroup>();
            }
            catch (Exception ex)
            {
                _provider.HostInteraction.Logger.Log(ex.ToString(), LogLevel.Error);
            }

            return libraryGroups;
        }
    }
}

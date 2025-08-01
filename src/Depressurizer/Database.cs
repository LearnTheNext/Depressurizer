﻿using CsvHelper;
using Depressurizer.Core;
using Depressurizer.Core.Enums;
using Depressurizer.Core.Helpers;
using Depressurizer.Core.Interfaces;
using Depressurizer.Core.Models;
using Depressurizer.Dialogs;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;

namespace Depressurizer
{
    public sealed class Database : IDatabase
    {
        #region Static Fields

        private static readonly object SyncRoot = new object();

        private static volatile Database _instance;

        #endregion

        #region Fields

        public readonly ConcurrentDictionary<long, DatabaseEntry> DatabaseEntries = new ConcurrentDictionary<long, DatabaseEntry>();

        private StoreLanguage _language = StoreLanguage.English;

        #endregion

        #region Constructors and Destructors

        private Database() { }

        #endregion

        #region Public Properties

        public static Database Instance
        {
            get
            {
                if (_instance != null)
                {
                    return _instance;
                }

                lock (SyncRoot)
                {
                    if (_instance == null)
                    {
                        _instance = new Database();
                    }
                }

                return _instance;
            }
        }

        [JsonIgnore]
        public SortedSet<string> AllFlags
        {
            get
            {
                SortedSet<string> flags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (DatabaseEntry entry in Values)
                {
                    flags.UnionWith(entry.Flags);
                }

                return flags;
            }
        }

        [JsonIgnore]
        public SortedSet<string> AllGenres
        {
            get
            {
                SortedSet<string> genres = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (DatabaseEntry entry in Values)
                {
                    genres.UnionWith(entry.Genres);
                }

                return genres;
            }
        }

        [JsonIgnore]
        public LanguageSupport AllLanguages
        {
            get
            {
                LanguageSupport languageSupport = new LanguageSupport();

                // ReSharper disable InconsistentNaming
                SortedSet<string> FullAudio = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                SortedSet<string> Interface = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                SortedSet<string> Subtitles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                // ReSharper restore InconsistentNaming

                foreach (DatabaseEntry entry in Values)
                {
                    FullAudio.UnionWith(entry.LanguageSupport.FullAudio);
                    Interface.UnionWith(entry.LanguageSupport.Interface);
                    Subtitles.UnionWith(entry.LanguageSupport.Subtitles);
                }

                languageSupport.FullAudio = FullAudio.ToList();
                languageSupport.Interface = Interface.ToList();
                languageSupport.Subtitles = Subtitles.ToList();

                return languageSupport;
            }
        }

        [JsonIgnore]
        public VRSupport AllVRSupport
        {
            get
            {
                VRSupport vrSupport = new VRSupport();

                // ReSharper disable InconsistentNaming
                SortedSet<string> Headsets = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                SortedSet<string> Input = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                SortedSet<string> PlayArea = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                // ReSharper restore InconsistentNaming

                foreach (DatabaseEntry entry in Values)
                {
                    Headsets.UnionWith(entry.VRSupport.Headsets);
                    Input.UnionWith(entry.VRSupport.Input);
                    PlayArea.UnionWith(entry.VRSupport.PlayArea);
                }

                vrSupport.Headsets = Headsets.ToList();
                vrSupport.Input = Input.ToList();
                vrSupport.PlayArea = PlayArea.ToList();

                return vrSupport;
            }
        }

        [JsonIgnore]
        public int Count => DatabaseEntries.Count;

        [JsonIgnore]
        public CultureInfo Culture { get; private set; }

        public StoreLanguage Language
        {
            get => _language;
            set
            {
                _language = value;
                Culture = Core.Helpers.Language.GetCultureInfo(_language);
                LanguageCode = Core.Helpers.Language.LanguageCode(_language);
            }
        }

        [JsonIgnore]
        public string LanguageCode { get; private set; }

        public long LastHLTBUpdate { get; set; }

        [JsonIgnore]
        public ICollection<DatabaseEntry> Values => DatabaseEntries.Values;

        #endregion

        #region Properties

        private static Logger Logger => Logger.Instance;

        private static Settings Settings => Settings.Instance;

        #endregion

        #region Public Methods and Operators

        public void Add(DatabaseEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            DatabaseEntries.AddOrUpdate(entry.Id, entry, (i, entry1) => entry1.MergeIn(entry));
        }

        public Dictionary<string, int> CalculateSortedDevList(IGameList gameList, int minCount)
        {
            Dictionary<string, int> devCounts = new Dictionary<string, int>();
            if (gameList == null)
            {
                foreach (DatabaseEntry entry in Values)
                {
                    CalculateSortedDevListHelper(devCounts, entry);
                }
            }
            else
            {
                foreach (int appId in gameList.Games.Keys)
                {
                    if (Contains(appId, out DatabaseEntry entry) && !gameList.Games[appId].IsHidden)
                    {
                        CalculateSortedDevListHelper(devCounts, entry);
                    }
                }
            }

            return devCounts.Where(e => e.Value >= minCount).ToDictionary(p => p.Key, p => p.Value);
        }

        public Dictionary<string, int> CalculateSortedPubList(IGameList filter, int minCount)
        {
            Dictionary<string, int> pubCounts = new Dictionary<string, int>();
            if (filter == null)
            {
                foreach (DatabaseEntry entry in Values)
                {
                    CalculateSortedPubListHelper(pubCounts, entry);
                }
            }
            else
            {
                foreach (long appId in filter.Games.Keys)
                {
                    if (!Contains(appId, out DatabaseEntry entry) || filter.Games[appId].IsHidden)
                    {
                        continue;
                    }

                    CalculateSortedPubListHelper(pubCounts, entry);
                }
            }

            return pubCounts.Where(e => e.Value >= minCount).ToDictionary(p => p.Key, p => p.Value);
        }

        public Dictionary<string, float> CalculateSortedTagList(IGameList filter, float weightFactor, int minScore, int tagsPerGame, bool excludeGenres, bool scoreSort)
        {
            Dictionary<string, float> tagCounts = new Dictionary<string, float>();
            if (filter == null)
            {
                foreach (DatabaseEntry entry in Values)
                {
                    CalculateSortedTagListHelper(tagCounts, entry, weightFactor, tagsPerGame);
                }
            }
            else
            {
                foreach (int appId in filter.Games.Keys)
                {
                    if (Contains(appId, out DatabaseEntry entry) && !filter.Games[appId].IsHidden)
                    {
                        CalculateSortedTagListHelper(tagCounts, entry, weightFactor, tagsPerGame);
                    }
                }
            }

            if (excludeGenres)
            {
                foreach (string genre in AllGenres)
                {
                    tagCounts.Remove(genre);
                }
            }

            IEnumerable<KeyValuePair<string, float>> unsorted = tagCounts.Where(e => e.Value >= minScore);
            if (scoreSort)
            {
                return unsorted.OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
            }

            return unsorted.OrderBy(e => e.Key).ToDictionary(e => e.Key, e => e.Value);
        }

        public void ChangeLanguage(StoreLanguage language)
        {
            StoreLanguage dbLang = language;
            if (Language == dbLang)
            {
                return;
            }

            Language = dbLang;
            //clean DB from data in wrong language
            foreach (DatabaseEntry g in Values)
            {
                if (g.Id <= 0)
                {
                    continue;
                }

                g.Tags = null;
                g.Flags = null;
                g.Genres = null;
                g.SteamReleaseDate = null;
                g.LastStoreScrape = 1; //pretend it is really old data
                g.VRSupport = new VRSupport();
                g.LanguageSupport = new LanguageSupport();
            }

            // Update DB with data in correct language
            if (FormMain.CurrentProfile != null)
            {
                List<ScrapeJob> scrapeJobs = new List<ScrapeJob>();
                scrapeJobs.AddRange(FormMain.CurrentProfile.GameData.Games.Values.Where(g => g.Id > 0 && !Settings.IgnoreList.Contains(g.Id)).Select(gameInfo => new ScrapeJob(gameInfo.Id, gameInfo.Id)));

                if (scrapeJobs.Count > 0)
                {
                    using (ScrapeDialog dialog = new ScrapeDialog(scrapeJobs))
                    {
                        dialog.ShowDialog();
                    }
                }
            }

            Save();
        }

        public void Clear()
        {
            DatabaseEntries.Clear();
        }

        public bool Contains(long appId)
        {
            return DatabaseEntries.ContainsKey(appId);
        }

        public bool Contains(long appId, out DatabaseEntry entry)
        {
            return DatabaseEntries.TryGetValue(appId, out entry);
        }

        /// <summary>
        ///     Fetches and integrates the complete list of public apps.
        /// </summary>
        /// <returns>
        ///     The number of new entries.
        /// </returns>
        public int FetchIntegrateAppList()
        {
            int added = 0;
            int updated = 0;

            HttpClient client = null;
            Stream stream = null;
            StreamReader streamReader = null;

            try
            {
                Logger.Info("Database: Downloading list of public apps.");

                client = new HttpClient();
                stream = client.GetStreamAsync(Constants.GetAppList).Result;
                streamReader = new StreamReader(stream);

                using (JsonReader reader = new JsonTextReader(streamReader))
                {
                    streamReader = null;
                    stream = null;
                    client = null;

                    Logger.Info("Database: Downloaded list of public apps.");
                    Logger.Info("Database: Parsing list of public apps.");

                    JsonSerializer serializer = new JsonSerializer();
                    AppList_RawData rawData = serializer.Deserialize<AppList_RawData>(reader);

                    foreach (App app in rawData.Applist.Apps)
                    {
                        if (Contains(app.AppId, out DatabaseEntry entry))
                        {
                            if (!string.IsNullOrWhiteSpace(entry.Name) && entry.Name == app.Name)
                            {
                                continue;
                            }

                            entry.Name = app.Name;
                            entry.AppType = AppType.Unknown;

                            updated++;
                        }
                        else
                        {
                            entry = new DatabaseEntry(app.AppId)
                            {
                                Name = app.Name
                            };

                            Add(entry);

                            added++;
                        }
                    }
                }
            }
            finally
            {
                streamReader?.Dispose();
                stream?.Dispose();
                client?.Dispose();
            }

            Logger.Info("Database: Parsed list of public apps, added {0} apps and updated {1} apps.", added, updated);

            return added;
        }

        public SortedSet<string> GetDevelopers(long appId)
        {
            return GetDevelopers(appId, 3);
        }

        public SortedSet<string> GetDevelopers(long appId, int depth)
        {
            if (!Contains(appId, out DatabaseEntry entry))
            {
                return new SortedSet<string>();
            }

            SortedSet<string> result = entry.Developers ?? new SortedSet<string>();
            if (result.Count == 0 && depth > 0 && entry.ParentId > 0)
            {
                result = GetDevelopers(entry.ParentId, depth - 1);
            }

            return result;
        }

        public SortedSet<string> GetFlagList(long appId)
        {
            return GetFlagList(appId, 3);
        }

        public SortedSet<string> GetFlagList(long appId, int depth)
        {
            if (!Contains(appId, out DatabaseEntry entry))
            {
                return new SortedSet<string>();
            }

            SortedSet<string> result = entry.Flags ?? new SortedSet<string>();
            if (result.Count == 0 && depth > 0 && entry.ParentId > 0)
            {
                result = GetFlagList(entry.ParentId, depth - 1);
            }

            return result;
        }

        public SortedSet<string> GetGenreList(long appId, int depth, bool tagFallback)
        {
            if (!Contains(appId, out DatabaseEntry entry))
            {
                return new SortedSet<string>();
            }

            SortedSet<string> result = entry.Genres ?? new SortedSet<string>();
            if (tagFallback && result.Count == 0)
            {
                SortedSet<string> tags = GetTagList(appId, 0);
                if (tags != null && tags.Count > 0)
                {
                    result = new SortedSet<string>(tags.Where(tag => AllGenres.Contains(tag)).ToList());
                }
            }

            if (result.Count == 0 && depth > 0 && entry.ParentId > 0)
            {
                result = GetGenreList(entry.ParentId, depth - 1, tagFallback);
            }

            return result;
        }

        public string GetName(long appId)
        {
            if (Contains(appId, out DatabaseEntry entry))
            {
                return entry.Name;
            }

            return string.Empty;
        }

        public SortedSet<string> GetPublishers(long appId)
        {
            return GetPublishers(appId, 3);
        }

        public SortedSet<string> GetPublishers(long appId, int depth)
        {
            if (!Contains(appId, out DatabaseEntry entry))
            {
                return new SortedSet<string>();
            }

            SortedSet<string> result = entry.Publishers ?? new SortedSet<string>();
            if (result.Count == 0 && depth > 0 && entry.ParentId > 0)
            {
                result = GetPublishers(entry.ParentId, depth - 1);
            }

            return result;
        }

        public int GetReleaseYear(long appId)
        {
            if (!Contains(appId, out DatabaseEntry entry))
            {
                return 0;
            }

            if (DateTime.TryParse(entry.SteamReleaseDate, out DateTime releaseDate))
            {
                return releaseDate.Year;
            }

            return 0;
        }

        public SortedSet<string> GetTagList(long appId)
        {
            return GetTagList(appId, 3);
        }

        public SortedSet<string> GetTagList(long appId, int depth)
        {
            if (!Contains(appId, out DatabaseEntry entry))
            {
                return new SortedSet<string>();
            }

            SortedSet<string> tags = entry.Tags ?? new SortedSet<string>();
            if (tags.Count == 0 && depth > 0 && entry.ParentId > 0)
            {
                tags = GetTagList(entry.ParentId, depth - 1);
            }

            return tags;
        }

        public bool IncludeItemInGameList(long appId)
        {
            if (!Contains(appId, out DatabaseEntry entry))
            {
                return false;
            }

            return entry.AppType == AppType.Application || entry.AppType == AppType.Game || entry.AppType == AppType.Mod;
        }

        public bool IsType(long appId, AppType appType)
        {
            if (!Contains(appId, out DatabaseEntry entry))
            {
                return false;
            }

            return entry.AppType == appType;
        }

        public void Load()
        {
            Load(Locations.File.Database);
        }

        public void Load(string path)
        {
            lock (SyncRoot)
            {
                Logger.Info("Database: Loading database from '{0}'.", path);
                if (!File.Exists(path))
                {
                    Logger.Warn("Database: Database file not found at '{0}'.", path);
                    return;
                }

                Stopwatch sw = new Stopwatch();
                sw.Start();

                using (StreamReader file = File.OpenText(path))
                {
                    JsonSerializer serializer = new JsonSerializer
                    {
#if DEBUG
                        Formatting = Formatting.Indented
#endif
                    };

                    Database database = (Database)serializer.Deserialize(file, typeof(Database));
                    if (database == null)
                    {
                        Logger.Warn("Database: Database file at '{0}' is corrupt.", path);
                        return;
                    }

                    Language = database.Language;
                    LastHLTBUpdate = database.LastHLTBUpdate;
                    foreach (DatabaseEntry entry in database.DatabaseEntries.Values)
                    {
                        Add(entry);
                    }
                }

                sw.Stop();
                Logger.Info("Database: Loaded database from '{0}', in {1}ms.", path, sw.ElapsedMilliseconds);
            }
        }

        public bool Remove(long appId)
        {
            return Remove(appId, out _);
        }

        public bool Remove(long appId, out DatabaseEntry entry)
        {
            return DatabaseEntries.TryRemove(appId, out entry);
        }

        public void Reset()
        {
            lock (SyncRoot)
            {
                DatabaseEntries.Clear();
                _language = StoreLanguage.English;
                Logger.Info("Database: Database was reset.");
            }
        }
        public void Save()
        {
            Save(Locations.File.Database);
        }

        public void Save(string path)
        {
            lock (SyncRoot)
            {
                Logger.Info("Database: Saving database to '{0}'.", path);

                Stopwatch sw = new Stopwatch();
                sw.Start();

                using (StreamWriter file = File.CreateText(path))
                {
                    JsonSerializer serializer = new JsonSerializer
                    {
#if DEBUG
                        Formatting = Formatting.Indented
#endif
                    };

                    serializer.Serialize(file, _instance);
                }

                sw.Stop();
                Logger.Info("Database: Saved database to '{0}', in {1}ms.", path, sw.ElapsedMilliseconds);
            }
        }

        public bool SupportsVR(long appId)
        {
            return SupportsVR(appId, 3);
        }

        public bool SupportsVR(long appId, int depth)
        {
            if (!Contains(appId, out DatabaseEntry entry))
            {
                return false;
            }

            VRSupport vrSupport = entry.VRSupport;
            if (vrSupport.Headsets != null && vrSupport.Headsets.Count > 0 || vrSupport.Input != null && vrSupport.Input.Count > 0 || vrSupport.PlayArea != null && vrSupport.PlayArea.Count > 0 && depth > 0 && entry.ParentId > 0)
            {
                return true;
            }

            if (depth > 0 && entry.ParentId > 0)
            {
                return SupportsVR(entry.ParentId, depth - 1);
            }

            return false;
        }

        /// <summary>
        ///     Updated the database with information from the AppInfo cache file.
        /// </summary>
        /// <param name="path">Path to the cache file</param>
        /// <returns>The number of entries integrated into the database.</returns>
        public int UpdateFromAppInfo(string path)
        {
            int updated = 0;

            Dictionary<int, AppInfo> appInfos = AppInfo.LoadApps(path);
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (AppInfo aInf in appInfos.Values)
            {
                if (!Contains(aInf.Id, out DatabaseEntry entry))
                {
                    entry = new DatabaseEntry(aInf.Id);
                    Add(entry);
                }

                entry.LastAppInfoUpdate = timestamp;
                if (aInf.AppType != AppType.Unknown)
                {
                    entry.AppType = aInf.AppType;
                }

                if (!string.IsNullOrEmpty(aInf.Name))
                {
                    entry.Name = aInf.Name;
                }

                if (entry.Platforms == AppPlatforms.None || entry.LastStoreScrape == 0 && aInf.Platforms > AppPlatforms.None)
                {
                    entry.Platforms = aInf.Platforms;
                }

                if (aInf.ParentId > 0)
                {
                    entry.ParentId = aInf.ParentId;
                }

                updated++;
            }

            return updated;
        }

        public int UpdateFromHLTB(bool includeImputedTimes)
        {
            int updated = 0;

            using (WebClient client = new WebClient())
            {
                client.Encoding = Encoding.UTF8;
                string result = client.DownloadString(Constants.HowLongToBeat);

                if (result.Contains("An error has occurred."))
                {
                    return updated;
                }

                HLTB_RawData rawData = new()
                {
                    Games = new()
                };

                using (var reader = new StringReader(result))
                using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
                {
                    foreach (dynamic record in csv.GetRecords<dynamic>())
                    {
                        if (!string.IsNullOrEmpty(record.steam_id))
                        {
                            rawData.Games.Add(new()
                            {
                                SteamAppData = new()
                                {
                                    SteamAppId = int.Parse(record.steam_id),
                                    SteamName = record.game_name,
                                    HltbInfo = new()
                                    {
                                        MainTtb = (int)Math.Ceiling(int.Parse(record.comp_main) / 60.0f),
                                        ExtrasTtb = (int)Math.Ceiling(int.Parse(record.comp_plus) / 60.0f),
                                        CompletionistTtb = (int)Math.Ceiling(int.Parse(record.comp_100) / 60.0f)
                                    }
                                }
                            });
                        }
                    }
                }

                foreach (Game game in rawData.Games)
                {
                    SteamAppData steamAppData = game.SteamAppData;
                    int id = steamAppData.SteamAppId;
                    if (!Contains(id, out DatabaseEntry entry))
                    {
                        continue;
                    }

                    HltbInfo info = steamAppData.HltbInfo;

                    if (!includeImputedTimes && info.MainTtbImputed)
                    {
                        entry.HltbMain = 0;
                    }
                    else
                    {
                        entry.HltbMain = info.MainTtb;
                    }

                    if (!includeImputedTimes && info.ExtrasTtbImputed)
                    {
                        entry.HltbExtras = 0;
                    }
                    else
                    {
                        entry.HltbExtras = info.ExtrasTtb;
                    }

                    if (!includeImputedTimes && info.CompletionistTtbImputed)
                    {
                        entry.HltbCompletionists = 0;
                    }
                    else
                    {
                        entry.HltbCompletionists = info.CompletionistTtb;
                    }

                    updated++;
                }
            }

            LastHLTBUpdate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            return updated;
        }

        #endregion

        #region Methods

        /// <summary>
        ///     Counts games for each developer.
        /// </summary>
        /// <param name="counts">
        ///     Existing dictionary of developers and game count. Key is the developer as a string, value is the
        ///     count
        /// </param>
        /// <param name="entry">Entry to add developers from</param>
        private static void CalculateSortedDevListHelper(IDictionary<string, int> counts, DatabaseEntry entry)
        {
            if (entry.Developers == null)
            {
                return;
            }

            foreach (string developer in entry.Developers)
            {
                if (counts.ContainsKey(developer))
                {
                    counts[developer] += 1;
                }
                else
                {
                    counts[developer] = 1;
                }
            }
        }

        /// <summary>
        ///     Counts games for each publisher.
        /// </summary>
        /// <param name="counts">
        ///     Existing dictionary of publishers and game count. Key is the publisher as a string, value is the
        ///     count
        /// </param>
        /// <param name="entry">Entry to add publishers from</param>
        private static void CalculateSortedPubListHelper(IDictionary<string, int> counts, DatabaseEntry entry)
        {
            if (entry.Publishers == null)
            {
                return;
            }

            foreach (string publisher in entry.Publishers)
            {
                if (counts.ContainsKey(publisher))
                {
                    counts[publisher] += 1;
                }
                else
                {
                    counts[publisher] = 1;
                }
            }
        }

        /// <summary>
        ///     Adds tags from the given DBEntry to the dictionary. Adds new elements if necessary, and increases values on
        ///     existing elements.
        /// </summary>
        /// <param name="counts">Existing dictionary of tags and scores. Key is the tag as a string, value is the score</param>
        /// <param name="entry">Entry to add tags from</param>
        /// <param name="weightFactor">
        ///     The score value of the first tag in the list.
        ///     The first tag on the game will have this score, and the last tag processed will always have score 1.
        ///     The tags between will have linearly interpolated values between them.
        /// </param>
        /// <param name="tagsPerGame"></param>
        private static void CalculateSortedTagListHelper(IDictionary<string, float> counts, DatabaseEntry entry, float weightFactor, int tagsPerGame)
        {
            if (entry.Tags == null)
            {
                return;
            }

            int tagsToLoad = tagsPerGame == 0 ? entry.Tags.Count : Math.Min(tagsPerGame, entry.Tags.Count);

            int i = 0;
            foreach (string tag in entry.Tags)
            {
                if (i >= tagsToLoad)
                {
                    continue;
                }

                // Get the score based on the weighting factor
                float score = 1;
                if (weightFactor > 1)
                {
                    if (tagsToLoad <= 1)
                    {
                        score = weightFactor;
                    }
                    else
                    {
                        float inter = i / (float) (tagsToLoad - 1);
                        score = (1 - inter) * weightFactor + inter;
                    }
                }

                if (counts.ContainsKey(tag))
                {
                    counts[tag] += score;
                }
                else
                {
                    counts[tag] = score;
                }

                i++;
            }
        }

        #endregion
    }
}

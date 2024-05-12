using Avalonia;
using Newtonsoft.Json;
using OF_DL.Entities;
using OF_DL.Entities.Archived;
using OF_DL.Entities.Messages;
using OF_DL.Entities.Post;
using OF_DL.Entities.Purchased;
using OF_DL.Entities.Streams;
using OF_DL.Enumurations;
using OF_DL.Helpers;
using Serilog;
using Spectre.Console;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using static OF_DL.Entities.Lists.UserList;

namespace OF_DL;

public class Program
{
    public static Auth? Auth { get; set; } = null;
    public static Config? Config { get; set; } = null;
    public int MAX_AGE = 0;
    public static List<long> paid_post_ids = new();
    private static readonly IAPIHelper m_ApiHelper;
    private static readonly IDBHelper m_DBHelper;
    private static readonly IDownloadHelper m_DownloadHelper;
    private static bool clientIdBlobMissing = false;
    private static bool devicePrivateKeyMissing = false;


    static Program()
    {
        m_ApiHelper = new APIHelper();
        m_DBHelper = new DBHelper();
        m_DownloadHelper = new DownloadHelper();
    }

    [STAThread]
    public static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        else
        {
            await ConsoleApp.Run();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<GuiApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private async static Task ConsoleMain()
	{
            await DownloadAllData();
    }


    private static async Task DownloadAllData()
    {
        do
        {
            DateTime startTime = DateTime.Now;
            Dictionary<string, int> users = new();
            Dictionary<string, int> activeSubs = await m_ApiHelper.GetActiveSubscriptions("/subscriptions/subscribes", Auth, Config.IncludeRestrictedSubscriptions);
            foreach (KeyValuePair<string, int> activeSub in activeSubs)
            {
                if (!users.ContainsKey(activeSub.Key))
                {
                    users.Add(activeSub.Key, activeSub.Value);
                }
            }
            if (Config!.IncludeExpiredSubscriptions)
            {
                Dictionary<string, int> expiredSubs = await m_ApiHelper.GetExpiredSubscriptions("/subscriptions/subscribes", Auth, Config.IncludeRestrictedSubscriptions);
                foreach (KeyValuePair<string, int> expiredSub in expiredSubs)
                {
                    if (!users.ContainsKey(expiredSub.Key))
                    {
                        users.Add(expiredSub.Key, expiredSub.Value);
                    }
                }
            }
            await m_DBHelper.CreateUsersDB(users);
            Dictionary<string, int> lists = await m_ApiHelper.GetLists("/lists", Auth);

            Dictionary<string, int> selectedUsers = new();
            KeyValuePair<bool, Dictionary<string, int>> hasSelectedUsersKVP;
            if(Config.NonInteractiveMode && Config.NonInteractiveModePurchasedTab)
            {
                hasSelectedUsersKVP = new KeyValuePair<bool, Dictionary<string, int>>(true, new Dictionary<string, int> { { "PurchasedTab", 0 } });
            }
            else if (Config.NonInteractiveMode && string.IsNullOrEmpty(Config.NonInteractiveModeListName))
            {
                hasSelectedUsersKVP = new KeyValuePair<bool, Dictionary<string, int>>(true, users);
            }
            else if (Config.NonInteractiveMode && !string.IsNullOrEmpty(Config.NonInteractiveModeListName))
            {
                List<string> listUsernames = new();
                int listId = lists[Config.NonInteractiveModeListName];
                List<string> usernames = await m_ApiHelper.GetListUsers($"/lists/{listId}/users", Auth);
                foreach (string user in usernames)
                {
                    listUsernames.Add(user);
                }
                selectedUsers = users.Where(x => listUsernames.Contains($"{x.Key}")).Distinct().ToDictionary(x => x.Key, x => x.Value);
                hasSelectedUsersKVP = new KeyValuePair<bool, Dictionary<string, int>>(true, selectedUsers);
            }
            else
            {
                hasSelectedUsersKVP = await HandleUserSelection(selectedUsers, users, lists);
            }

            if (hasSelectedUsersKVP.Key && hasSelectedUsersKVP.Value != null && hasSelectedUsersKVP.Value.ContainsKey("SinglePost"))
            {
                string postUrl = AnsiConsole.Prompt(
                        new TextPrompt<string>("[red]Please enter a post URL: [/]")
                            .ValidationErrorMessage("[red]Please enter a valid post URL[/]")
                            .Validate(url =>
                            {
                                Regex regex = new Regex("https://onlyfans\\.com/[0-9]+/[A-Za-z0-9]+", RegexOptions.IgnoreCase);
                                if (regex.IsMatch(url))
                                {
                                    return ValidationResult.Success();
                                }
                                return ValidationResult.Error("[red]Please enter a valid post URL[/]");
                            }));

                long post_id = Convert.ToInt64(postUrl.Split("/")[3]);
                string username = postUrl.Split("/")[4];

                if (users.ContainsKey(username))
                {
                    string path = "";
                    if (!string.IsNullOrEmpty(Config.DownloadPath))
                    {
                        path = System.IO.Path.Combine(Config.DownloadPath, username);
                    }
                    else
                    {
                        path = $"__user_data__/sites/OnlyFans/{username}"; // specify the path for the new folder
                    }

                    if (!Directory.Exists(path)) // check if the folder already exists
                    {
                        Directory.CreateDirectory(path); // create the new folder
                        AnsiConsole.Markup($"[red]Created folder for {username}\n[/]");
                    }
                    else
                    {
                        AnsiConsole.Markup($"[red]Folder for {username} already created\n[/]");
                    }

                    await m_DBHelper.CreateDB(path);

                    await DownloadSinglePost(post_id, path, users);
                }
            }
            else if (hasSelectedUsersKVP.Key && hasSelectedUsersKVP.Value != null && hasSelectedUsersKVP.Value.ContainsKey("PurchasedTab"))
            {
                Dictionary<string, int> purchasedTabUsers = await m_ApiHelper.GetPurchasedTabUsers("/posts/paid", Auth, Config, users);
                AnsiConsole.Markup($"[red]Checking folders for Users in Purchased Tab\n[/]");
                foreach (KeyValuePair<string, int> user in purchasedTabUsers)
                {
                    string path = "";
                    if (!string.IsNullOrEmpty(Config.DownloadPath))
                    {
                        path = System.IO.Path.Combine(Config.DownloadPath, user.Key);
                    }
                    else
                    {
                        path = $"__user_data__/sites/OnlyFans/{user.Key}";
                    }

                    await m_DBHelper.CheckUsername(user, path);

                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                        AnsiConsole.Markup($"[red]Created folder for {user.Key}\n[/]");
                    }
                    else
                    {
                        AnsiConsole.Markup($"[red]Folder for {user.Key} already created\n[/]");
                    }

                    Entities.User user_info = await m_ApiHelper.GetUserInfo($"/users/{user.Key}", Auth);

                    await m_DBHelper.CreateDB(path);
                }

                string p = "";
                if (!string.IsNullOrEmpty(Config.DownloadPath))
                {
                    p = Config.DownloadPath;
                }
                else
                {
                    p = $"__user_data__/sites/OnlyFans/";
                }
                List<PurchasedTabCollection> purchasedTabCollections = await m_ApiHelper.GetPurchasedTab("/posts/paid", p, Auth, Config, users);
                foreach(PurchasedTabCollection purchasedTabCollection in purchasedTabCollections)
                {
                    AnsiConsole.Markup($"[red]\nScraping Data for {purchasedTabCollection.Username}\n[/]");
                    string path = "";
                    if (!string.IsNullOrEmpty(Config.DownloadPath))
                    {
                        path = System.IO.Path.Combine(Config.DownloadPath, purchasedTabCollection.Username);
                    }
                    else
                    {
                        path = $"__user_data__/sites/OnlyFans/{purchasedTabCollection.Username}"; // specify the path for the new folder
                    }
                    int paidPostCount = 0;
                    int paidMessagesCount = 0;
                    paidPostCount = await DownloadPaidPostsPurchasedTab(purchasedTabCollection.PaidPosts, users.FirstOrDefault(u => u.Value == purchasedTabCollection.UserId), paidPostCount, path, users);
                    paidMessagesCount = await DownloadPaidMessagesPurchasedTab(purchasedTabCollection.PaidMessages, users.FirstOrDefault(u => u.Value == purchasedTabCollection.UserId), paidMessagesCount, path, users);

                    AnsiConsole.Markup("\n");
                    AnsiConsole.Write(new BreakdownChart()
                    .FullSize()
                    .AddItem("Paid Posts", paidPostCount, Color.Red)
                    .AddItem("Paid Messages", paidMessagesCount, Color.Aqua));
                    AnsiConsole.Markup("\n");
                }
                DateTime endTime = DateTime.Now;
                TimeSpan totalTime = endTime - startTime;
                AnsiConsole.Markup($"[green]Scrape Completed in {totalTime.TotalMinutes:0.00} minutes\n[/]");
            }
            else if (hasSelectedUsersKVP.Key && !hasSelectedUsersKVP.Value.ContainsKey("ConfigChanged"))
            {
                //Iterate over each user in the list of users
                foreach (KeyValuePair<string, int> user in hasSelectedUsersKVP.Value)
                {
                    int paidPostCount = 0;
                    int postCount = 0;
                    int archivedCount = 0;
                    int streamsCount = 0;
                    int storiesCount = 0;
                    int highlightsCount = 0;
                    int messagesCount = 0;
                    int paidMessagesCount = 0;
                    AnsiConsole.Markup($"[red]\nScraping Data for {user.Key}\n[/]");

                    string path = "";
                    if (!string.IsNullOrEmpty(Config.DownloadPath))
                    {
                        path = System.IO.Path.Combine(Config.DownloadPath, user.Key);
                    }
                    else
                    {
                        path = $"__user_data__/sites/OnlyFans/{user.Key}"; // specify the path for the new folder
                    }

                    await m_DBHelper.CheckUsername(user, path);

                    if (!Directory.Exists(path)) // check if the folder already exists
                    {
                        Directory.CreateDirectory(path); // create the new folder
                        AnsiConsole.Markup($"[red]Created folder for {user.Key}\n[/]");
                    }
                    else
                    {
                        AnsiConsole.Markup($"[red]Folder for {user.Key} already created\n[/]");
                    }

                    Entities.User user_info = await m_ApiHelper.GetUserInfo($"/users/{user.Key}", Auth);

                    await m_DBHelper.CreateDB(path);

                    if (Config.DownloadAvatarHeaderPhoto)
                    {
                        await m_DownloadHelper.DownloadAvatarHeader(user_info.avatar, user_info.header, path, user.Key);
                    }

                    if (Config.DownloadPaidPosts)
                    {
                        paidPostCount = await DownloadPaidPosts(hasSelectedUsersKVP, user, paidPostCount, path);
                    }

                    if (Config.DownloadPosts)
                    {
                        postCount = await DownloadFreePosts(hasSelectedUsersKVP, user, postCount, path);
                    }

                    if (Config.DownloadArchived)
                    {
                        archivedCount = await DownloadArchived(hasSelectedUsersKVP, user, archivedCount, path);
                    }

                    if (Config.DownloadStreams)
                    {
                        streamsCount = await DownloadStreams(hasSelectedUsersKVP, user, streamsCount, path);
                    }

                    if (Config.DownloadStories)
                    {
                        storiesCount = await DownloadStories(user, storiesCount, path);
                    }

                    if (Config.DownloadHighlights)
                    {
                        highlightsCount = await DownloadHighlights(user, highlightsCount, path);
                    }

                    if (Config.DownloadMessages)
                    {
                        messagesCount = await DownloadMessages(hasSelectedUsersKVP, user, messagesCount, path);
                    }

                    if (Config.DownloadPaidMessages)
                    {
                        paidMessagesCount = await DownloadPaidMessages(hasSelectedUsersKVP, user, paidMessagesCount, path);
                    }

                    AnsiConsole.Markup("\n");
                    AnsiConsole.Write(new BreakdownChart()
                    .FullSize()
                    .AddItem("Paid Posts", paidPostCount, Color.Red)
                    .AddItem("Posts", postCount, Color.Blue)
                    .AddItem("Archived", archivedCount, Color.Green)
                    .AddItem("Streams", streamsCount, Color.Purple)
                    .AddItem("Stories", storiesCount, Color.Yellow)
                    .AddItem("Highlights", highlightsCount, Color.Orange1)
                    .AddItem("Messages", messagesCount, Color.LightGreen)
                    .AddItem("Paid Messages", paidMessagesCount, Color.Aqua));
                    AnsiConsole.Markup("\n");
                }
                DateTime endTime = DateTime.Now;
                TimeSpan totalTime = endTime - startTime;
                AnsiConsole.Markup($"[green]Scrape Completed in {totalTime.TotalMinutes:0.00} minutes\n[/]");
            }
            else if (hasSelectedUsersKVP.Key && hasSelectedUsersKVP.Value != null && hasSelectedUsersKVP.Value.ContainsKey("ConfigChanged"))
            {
                continue;
            }
            else
            {
                break;
            }
        } while (!Config.NonInteractiveMode);
    }

    private static async Task<int> DownloadPaidMessages(KeyValuePair<bool, Dictionary<string, int>> hasSelectedUsersKVP, KeyValuePair<string, int> user, int paidMessagesCount, string path)
    {
        AnsiConsole.Markup($"[red]Getting Paid Messages\n[/]");
        //Dictionary<long, string> purchased = await apiHelper.GetMedia(MediaType.PaidMessages, "/posts/paid", user.Key, path, auth, paid_post_ids);
        PaidMessageCollection paidMessageCollection = await m_ApiHelper.GetPaidMessages("/posts/paid", path, user.Key, Auth!, Config!);
        int oldPaidMessagesCount = 0;
        int newPaidMessagesCount = 0;
        if (paidMessageCollection != null && paidMessageCollection.PaidMessages.Count > 0)
        {
            AnsiConsole.Markup($"[red]Found {paidMessageCollection.PaidMessages.Count} Paid Messages\n[/]");
            paidMessagesCount = paidMessageCollection.PaidMessages.Count;
            long totalSize = 0;
            if (Config.ShowScrapeSize)
            {
                totalSize = await m_DownloadHelper.CalculateTotalFileSize(paidMessageCollection.PaidMessages.Values.ToList(), Auth);
            }
            else
            {
                totalSize = paidMessagesCount;
            }
            await AnsiConsole.Progress()
            .Columns(GetProgressColumns(Config.ShowScrapeSize))
            .StartAsync(async ctx =>
            {
                // Define tasks
                var task = ctx.AddTask($"[red]Downloading {paidMessageCollection.PaidMessages.Count} Paid Messages[/]", autoStart: false);
                task.MaxValue = totalSize;
                task.StartTask();
                foreach (KeyValuePair<long, string> paidMessageKVP in paidMessageCollection.PaidMessages)
                {
                    bool isNew;
                    if (paidMessageKVP.Value.Contains("cdn3.onlyfans.com/dash/files"))
                    {
                        string[] messageUrlParsed = paidMessageKVP.Value.Split(',');
                        string mpdURL = messageUrlParsed[0];
                        string policy = messageUrlParsed[1];
                        string signature = messageUrlParsed[2];
                        string kvp = messageUrlParsed[3];
                        string mediaId = messageUrlParsed[4];
                        string messageId = messageUrlParsed[5];
                        string? licenseURL = null;
                        string? pssh = await m_ApiHelper.GetDRMMPDPSSH(mpdURL, policy, signature, kvp, Auth);
                        if (pssh != null)
                        {
                            DateTime lastModified = await m_ApiHelper.GetDRMMPDLastModified(mpdURL, policy, signature, kvp, Auth);
                            Dictionary<string, string> drmHeaders = await m_ApiHelper.GetDynamicHeaders($"/api2/v2/users/media/{mediaId}/drm/message/{messageId}", "?type=widevine", Auth);
                            string decryptionKey;
                            if (clientIdBlobMissing || devicePrivateKeyMissing)
                            {
                                decryptionKey = await m_ApiHelper.GetDecryptionKey(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/message/{messageId}?type=widevine", pssh, Auth);
                            }
                            else
                            {
                                decryptionKey = await m_ApiHelper.GetDecryptionKeyNew(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/message/{messageId}?type=widevine", pssh, Auth);
                            }


                            Purchased.Medium? mediaInfo = paidMessageCollection.PaidMessageMedia.FirstOrDefault(m => m.id == paidMessageKVP.Key);
                            Purchased.List? messageInfo = paidMessageCollection.PaidMessageObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                            isNew = await m_DownloadHelper.DownloadPurchasedMessageDRMVideo(
                                user_agent: Auth.USER_AGENT,
                                policy: policy,
                                signature: signature,
                                kvp: kvp,
                                sess: Auth.COOKIE,
                                url: mpdURL,
                                decryptionKey: decryptionKey,
                                folder: path,
                                lastModified: lastModified,
                                media_id: paidMessageKVP.Key,
                                task: task,
                                filenameFormat: !string.IsNullOrEmpty(Config.PaidMessageFileNameFormat) ? Config.PaidMessageFileNameFormat : string.Empty,
                                messageInfo: messageInfo,
                                messageMedia: mediaInfo,
                                fromUser: messageInfo.fromUser,
                                users: hasSelectedUsersKVP.Value,
                                config: Config,
                                showScrapeSize: Config.ShowScrapeSize);

                            if (isNew)
                            {
                                newPaidMessagesCount++;
                            }
                            else
                            {
                                oldPaidMessagesCount++;
                            }
                        }
                    }
                    else
                    {
                        Purchased.Medium? mediaInfo = paidMessageCollection.PaidMessageMedia.FirstOrDefault(m => m.id == paidMessageKVP.Key);
                        Purchased.List messageInfo = paidMessageCollection.PaidMessageObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                        isNew = await m_DownloadHelper.DownloadPurchasedMedia(
                            url: paidMessageKVP.Value,
                            folder: path,
                            media_id: paidMessageKVP.Key,
                            task: task,
                            filenameFormat: !string.IsNullOrEmpty(Config.PaidMessageFileNameFormat) ? Config.PaidMessageFileNameFormat : string.Empty,
                            messageInfo: messageInfo,
                            messageMedia: mediaInfo,
                            fromUser: messageInfo.fromUser,
                            users: hasSelectedUsersKVP.Value,
                            config: Config,
                            Config.ShowScrapeSize);
                        if (isNew)
                        {
                            newPaidMessagesCount++;
                        }
                        else
                        {
                            oldPaidMessagesCount++;
                        }
                    }
                }
                task.StopTask();
            });
            AnsiConsole.Markup($"[red]Paid Messages Already Downloaded: {oldPaidMessagesCount} New Paid Messages Downloaded: {newPaidMessagesCount}[/]\n");
        }
        else
        {
            AnsiConsole.Markup($"[red]Found 0 Paid Messages\n[/]");
        }

        return paidMessagesCount;
    }

    private static async Task<int> DownloadMessages(KeyValuePair<bool, Dictionary<string, int>> hasSelectedUsersKVP, KeyValuePair<string, int> user, int messagesCount, string path)
    {
        AnsiConsole.Markup($"[red]Getting Messages\n[/]");
        //Dictionary<long, string> messages = await apiHelper.GetMedia(MediaType.Messages, $"/chats/{user.Value}/messages", null, path, auth, paid_post_ids);
        MessageCollection messages = await m_ApiHelper.GetMessages($"/chats/{user.Value}/messages", path, Auth!, Config!);
        int oldMessagesCount = 0;
        int newMessagesCount = 0;
        if (messages != null && messages.Messages.Count > 0)
        {
            AnsiConsole.Markup($"[red]Found {messages.Messages.Count} Messages\n[/]");
            messagesCount = messages.Messages.Count;
            long totalSize = 0;
            if (Config.ShowScrapeSize)
            {
                totalSize = await m_DownloadHelper.CalculateTotalFileSize(messages.Messages.Values.ToList(), Auth);
            }
            else
            {
                totalSize = messagesCount;
            }
            await AnsiConsole.Progress()
            .Columns(GetProgressColumns(Config.ShowScrapeSize))
            .StartAsync(async ctx =>
            {
                // Define tasks
                var task = ctx.AddTask($"[red]Downloading {messages.Messages.Count} Messages[/]", autoStart: false);
                task.MaxValue = totalSize;
                task.StartTask();
                foreach (KeyValuePair<long, string> messageKVP in messages.Messages)
                {
                    bool isNew;
                    if (messageKVP.Value.Contains("cdn3.onlyfans.com/dash/files"))
                    {
                        string[] messageUrlParsed = messageKVP.Value.Split(',');
                        string mpdURL = messageUrlParsed[0];
                        string policy = messageUrlParsed[1];
                        string signature = messageUrlParsed[2];
                        string kvp = messageUrlParsed[3];
                        string mediaId = messageUrlParsed[4];
                        string messageId = messageUrlParsed[5];
                        string? licenseURL = null;
                        string? pssh = await m_ApiHelper.GetDRMMPDPSSH(mpdURL, policy, signature, kvp, Auth);
                        if (pssh != null)
                        {
                            DateTime lastModified = await m_ApiHelper.GetDRMMPDLastModified(mpdURL, policy, signature, kvp, Auth);
                            Dictionary<string, string> drmHeaders = await m_ApiHelper.GetDynamicHeaders($"/api2/v2/users/media/{mediaId}/drm/message/{messageId}", "?type=widevine", Auth);
                            string decryptionKey;
                            if (clientIdBlobMissing || devicePrivateKeyMissing)
                            {
                                decryptionKey = await m_ApiHelper.GetDecryptionKey(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/message/{messageId}?type=widevine", pssh, Auth);
                            }
                            else
                            {
                                decryptionKey = await m_ApiHelper.GetDecryptionKeyNew(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/message/{messageId}?type=widevine", pssh, Auth);
                            }
                            Messages.Medium? mediaInfo = messages.MessageMedia.FirstOrDefault(m => m.id == messageKVP.Key);
                            Messages.List? messageInfo = messages.MessageObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                            isNew = await m_DownloadHelper.DownloadMessageDRMVideo(
                                user_agent: Auth.USER_AGENT,
                                policy: policy,
                                signature: signature,
                                kvp: kvp,
                                sess: Auth.COOKIE,
                                url: mpdURL,
                                decryptionKey: decryptionKey,
                                folder: path,
                                lastModified: lastModified,
                                media_id: messageKVP.Key,
                                task: task,
                                filenameFormat: !string.IsNullOrEmpty(Config.MessageFileNameFormat) ? Config.MessageFileNameFormat : string.Empty,
                                messageInfo: messageInfo,
                                messageMedia: mediaInfo,
                                fromUser: messageInfo.fromUser,
                                users: hasSelectedUsersKVP.Value,
                                config: Config,
                                showScrapeSize: Config.ShowScrapeSize);


                            if (isNew)
                            {
                                newMessagesCount++;
                            }
                            else
                            {
                                oldMessagesCount++;
                            }
                        }
                    }
                    else
                    {
                        Messages.Medium? mediaInfo = messages.MessageMedia.FirstOrDefault(m => m.id == messageKVP.Key);
                        Messages.List? messageInfo = messages.MessageObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                        isNew = await m_DownloadHelper.DownloadMessageMedia(
                            url: messageKVP.Value,
                            folder: path,
                            media_id: messageKVP.Key,
                            task: task,
                            filenameFormat: !string.IsNullOrEmpty(Config!.MessageFileNameFormat) ? Config.MessageFileNameFormat : string.Empty,
                            messageInfo: messageInfo,
                            messageMedia: mediaInfo,
                            fromUser: messageInfo.fromUser,
                            users: hasSelectedUsersKVP.Value,
                            config: Config,
                            Config.ShowScrapeSize);

                        if (isNew)
                        {
                            newMessagesCount++;
                        }
                        else
                        {
                            oldMessagesCount++;
                        }
                    }
                }
                task.StopTask();
            });
            AnsiConsole.Markup($"[red]Messages Already Downloaded: {oldMessagesCount} New Messages Downloaded: {newMessagesCount}[/]\n");
        }
        else
        {
            AnsiConsole.Markup($"[red]Found 0 Messages\n[/]");
        }

        return messagesCount;
    }

    private static async Task<int> DownloadHighlights(KeyValuePair<string, int> user, int highlightsCount, string path)
    {
        AnsiConsole.Markup($"[red]Getting Highlights\n[/]");
        Dictionary<long, string> highlights = await m_ApiHelper.GetMedia(MediaType.Highlights, $"/users/{user.Value}/stories/highlights", null, path, Auth!, Config!, paid_post_ids);
        int oldHighlightsCount = 0;
        int newHighlightsCount = 0;
        if (highlights != null && highlights.Count > 0)
        {
            AnsiConsole.Markup($"[red]Found {highlights.Count} Highlights\n[/]");
            highlightsCount = highlights.Count;
            long totalSize = 0;
            if (Config.ShowScrapeSize)
            {
                totalSize = await m_DownloadHelper.CalculateTotalFileSize(highlights.Values.ToList(), Auth);
            }
            else
            {
                totalSize = highlightsCount;
            }
            await AnsiConsole.Progress()
            .Columns(GetProgressColumns(Config.ShowScrapeSize))
            .StartAsync(async ctx =>
            {
                // Define tasks
                var task = ctx.AddTask($"[red]Downloading {highlights.Count} Highlights[/]", autoStart: false);
                task.MaxValue = totalSize;
                task.StartTask();
                foreach (KeyValuePair<long, string> highlightKVP in highlights)
                {
                    bool isNew = await m_DownloadHelper.DownloadStoryMedia(highlightKVP.Value, path, highlightKVP.Key, task, Config!, Config.ShowScrapeSize);
                    if (isNew)
                    {
                        newHighlightsCount++;
                    }
                    else
                    {
                        oldHighlightsCount++;
                    }
                }
                task.StopTask();
            });
            AnsiConsole.Markup($"[red]Highlights Already Downloaded: {oldHighlightsCount} New Highlights Downloaded: {newHighlightsCount}[/]\n");
        }
        else
        {
            AnsiConsole.Markup($"[red]Found 0 Highlights\n[/]");
        }

        return highlightsCount;
    }

    private static async Task<int> DownloadStories(KeyValuePair<string, int> user, int storiesCount, string path)
    {
        AnsiConsole.Markup($"[red]Getting Stories\n[/]");
        Dictionary<long, string> stories = await m_ApiHelper.GetMedia(MediaType.Stories, $"/users/{user.Value}/stories", null, path, Auth!, Config!, paid_post_ids);
        int oldStoriesCount = 0;
        int newStoriesCount = 0;
        if (stories != null && stories.Count > 0)
        {
            AnsiConsole.Markup($"[red]Found {stories.Count} Stories\n[/]");
            storiesCount = stories.Count;
            long totalSize = 0;
            if (Config.ShowScrapeSize)
            {
                totalSize = await m_DownloadHelper.CalculateTotalFileSize(stories.Values.ToList(), Auth);
            }
            else
            {
                totalSize = storiesCount;
            }
            await AnsiConsole.Progress()
            .Columns(GetProgressColumns(Config.ShowScrapeSize))
            .StartAsync(async ctx =>
            {
                // Define tasks
                var task = ctx.AddTask($"[red]Downloading {stories.Count} Stories[/]", autoStart: false);
                task.MaxValue = totalSize;
                task.StartTask();
                foreach (KeyValuePair<long, string> storyKVP in stories)
                {
                    bool isNew = await m_DownloadHelper.DownloadStoryMedia(storyKVP.Value, path, storyKVP.Key, task, Config!, Config.ShowScrapeSize);
                    if (isNew)
                    {
                        newStoriesCount++;
                    }
                    else
                    {
                        oldStoriesCount++;
                    }
                }
                task.StopTask();
            });
            AnsiConsole.Markup($"[red]Stories Already Downloaded: {oldStoriesCount} New Stories Downloaded: {newStoriesCount}[/]\n");
        }
        else
        {
            AnsiConsole.Markup($"[red]Found 0 Stories\n[/]");
        }

        return storiesCount;
    }

    private static async Task<int> DownloadArchived(KeyValuePair<bool, Dictionary<string, int>> hasSelectedUsersKVP, KeyValuePair<string, int> user, int archivedCount, string path)
    {
        AnsiConsole.Markup($"[red]Getting Archived Posts\n[/]");
        //Dictionary<long, string> archived = await apiHelper.GetMedia(MediaType.Archived, $"/users/{user.Value}/posts", null, path, auth, paid_post_ids);
        ArchivedCollection archived = await m_ApiHelper.GetArchived($"/users/{user.Value}/posts", path, Auth!, Config!);
        int oldArchivedCount = 0;
        int newArchivedCount = 0;
        if (archived != null && archived.ArchivedPosts.Count > 0)
        {
            AnsiConsole.Markup($"[red]Found {archived.ArchivedPosts.Count} Archived Posts\n[/]");
            archivedCount = archived.ArchivedPosts.Count;
            long totalSize = 0;
            if (Config.ShowScrapeSize)
            {
                totalSize = await m_DownloadHelper.CalculateTotalFileSize(archived.ArchivedPosts.Values.ToList(), Auth);
            }
            else
            {
                totalSize = archivedCount;
            }
            await AnsiConsole.Progress()
            .Columns(GetProgressColumns(Config.ShowScrapeSize))
            .StartAsync(async ctx =>
            {
                // Define tasks
                var task = ctx.AddTask($"[red]Downloading {archived.ArchivedPosts.Count} Archived Posts[/]", autoStart: false);
                task.MaxValue = totalSize;
                task.StartTask();
                foreach (KeyValuePair<long, string> archivedKVP in archived.ArchivedPosts)
                {
                    bool isNew;
                    if (archivedKVP.Value.Contains("cdn3.onlyfans.com/dash/files"))
                    {
                        string[] messageUrlParsed = archivedKVP.Value.Split(',');
                        string mpdURL = messageUrlParsed[0];
                        string policy = messageUrlParsed[1];
                        string signature = messageUrlParsed[2];
                        string kvp = messageUrlParsed[3];
                        string mediaId = messageUrlParsed[4];
                        string postId = messageUrlParsed[5];
                        string? licenseURL = null;
                        string? pssh = await m_ApiHelper.GetDRMMPDPSSH(mpdURL, policy, signature, kvp, Auth);
                        if (pssh != null)
                        {
                            DateTime lastModified = await m_ApiHelper.GetDRMMPDLastModified(mpdURL, policy, signature, kvp, Auth);
                            Dictionary<string, string> drmHeaders = await m_ApiHelper.GetDynamicHeaders($"/api2/v2/users/media/{mediaId}/drm/post/{postId}", "?type=widevine", Auth);
                            string decryptionKey;
                            if (clientIdBlobMissing || devicePrivateKeyMissing)
                            {
                                decryptionKey = await m_ApiHelper.GetDecryptionKey(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/post/{postId}?type=widevine", pssh, Auth);
                            }
                            else
                            {
                                decryptionKey = await m_ApiHelper.GetDecryptionKeyNew(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/post/{postId}?type=widevine", pssh, Auth);
                            }
                            Archived.Medium? mediaInfo = archived.ArchivedPostMedia.FirstOrDefault(m => m.id == archivedKVP.Key);
                            Archived.List? postInfo = archived.ArchivedPostObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                            isNew = await m_DownloadHelper.DownloadArchivedPostDRMVideo(
                                user_agent: Auth.USER_AGENT,
                                policy: policy,
                                signature: signature,
                                kvp: kvp,
                                sess: Auth.COOKIE,
                                url: mpdURL,
                                decryptionKey: decryptionKey,
                                folder: path,
                                lastModified: lastModified,
                                media_id: archivedKVP.Key,
                                task: task,
                                filenameFormat: !string.IsNullOrEmpty(Config.PostFileNameFormat) ? Config.PostFileNameFormat : string.Empty,
                                postInfo: !string.IsNullOrEmpty(Config.PostFileNameFormat) ? postInfo : null,
                                postMedia: !string.IsNullOrEmpty(Config.PostFileNameFormat) ? mediaInfo : null,
                                author: !string.IsNullOrEmpty(Config.PostFileNameFormat) ? postInfo.author : null,
                                users: hasSelectedUsersKVP.Value,
                                config: Config,
                                showScrapeSize: Config.ShowScrapeSize);

                            if (isNew)
                            {
                                newArchivedCount++;
                            }
                            else
                            {
                                oldArchivedCount++;
                            }
                        }
                    }
                    else
                    {
                        Archived.Medium? mediaInfo = archived.ArchivedPostMedia.FirstOrDefault(m => m.id == archivedKVP.Key);
                        Archived.List? postInfo = archived.ArchivedPostObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                        isNew = await m_DownloadHelper.DownloadArchivedMedia(
                            url: archivedKVP.Value,
                            folder: path,
                            media_id: archivedKVP.Key,
                            task: task,
                            filenameFormat: !string.IsNullOrEmpty(Config.PostFileNameFormat) ? Config.PostFileNameFormat : string.Empty,
                            messageInfo: !string.IsNullOrEmpty(Config.PostFileNameFormat) ? postInfo : null,
                            messageMedia: !string.IsNullOrEmpty(Config.PostFileNameFormat) ? mediaInfo : null,
                            author: !string.IsNullOrEmpty(Config.PostFileNameFormat) ? postInfo.author : null,
                            users: hasSelectedUsersKVP.Value,
                            config: Config,
                            Config.ShowScrapeSize);

                        if (isNew)
                        {
                            newArchivedCount++;
                        }
                        else
                        {
                            oldArchivedCount++;
                        }
                    }
                }
                task.StopTask();
            });
            AnsiConsole.Markup($"[red]Archived Posts Already Downloaded: {oldArchivedCount} New Archived Posts Downloaded: {newArchivedCount}[/]\n");
        }
        else
        {
            AnsiConsole.Markup($"[red]Found 0 Archived Posts\n[/]");
        }

        return archivedCount;
    }

    private static async Task<int> DownloadFreePosts(KeyValuePair<bool, Dictionary<string, int>> hasSelectedUsersKVP, KeyValuePair<string, int> user, int postCount, string path)
    {
        AnsiConsole.Markup($"[red]Getting Posts\n[/]");
        //Dictionary<long, string> posts = await apiHelper.GetMedia(MediaType.Posts, $"/users/{user.Value}/posts", null, path, auth, paid_post_ids);
        PostCollection posts = await m_ApiHelper.GetPosts($"/users/{user.Value}/posts", path, Auth!, Config!, paid_post_ids);
        int oldPostCount = 0;
        int newPostCount = 0;
        if (posts == null || posts.Posts.Count <= 0)
        {
            AnsiConsole.Markup($"[red]Found 0 Posts\n[/]");
            return 0;
        }

        AnsiConsole.Markup($"[red]Found {posts.Posts.Count} Posts\n[/]");
        postCount = posts.Posts.Count;
        long totalSize = 0;
        if (Config.ShowScrapeSize)
        {
            totalSize = await m_DownloadHelper.CalculateTotalFileSize(posts.Posts.Values.ToList(), Auth);
        }
        else
        {
            totalSize = postCount;
        }
        await AnsiConsole.Progress()
        .Columns(GetProgressColumns(Config.ShowScrapeSize))
        .StartAsync(async ctx =>
        {
            var task = ctx.AddTask($"[red]Downloading {posts.Posts.Count} Posts[/]", autoStart: false);
            task.MaxValue = totalSize;
            task.StartTask();
            foreach (KeyValuePair<long, string> postKVP in posts.Posts)
            {
                bool isNew;
                if (postKVP.Value.Contains("cdn3.onlyfans.com/dash/files"))
                {
                    string[] messageUrlParsed = postKVP.Value.Split(',');
                    string mpdURL = messageUrlParsed[0];
                    string policy = messageUrlParsed[1];
                    string signature = messageUrlParsed[2];
                    string kvp = messageUrlParsed[3];
                    string mediaId = messageUrlParsed[4];
                    string postId = messageUrlParsed[5];
                    string? licenseURL = null;
                    string? pssh = await m_ApiHelper.GetDRMMPDPSSH(mpdURL, policy, signature, kvp, Auth);
                    if (pssh == null)
                    {
                        continue;
                    }

                    DateTime lastModified = await m_ApiHelper.GetDRMMPDLastModified(mpdURL, policy, signature, kvp, Auth);
                    Dictionary<string, string> drmHeaders = await m_ApiHelper.GetDynamicHeaders($"/api2/v2/users/media/{mediaId}/drm/post/{postId}", "?type=widevine", Auth);
                    string decryptionKey;
                    if (clientIdBlobMissing || devicePrivateKeyMissing)
                    {
                        decryptionKey = await m_ApiHelper.GetDecryptionKey(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/post/{postId}?type=widevine", pssh, Auth);
                    }
                    else
                    {
                        decryptionKey = await m_ApiHelper.GetDecryptionKeyNew(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/post/{postId}?type=widevine", pssh, Auth);
                    }
                    Post.Medium mediaInfo = posts.PostMedia.FirstOrDefault(m => m.id == postKVP.Key);
                    Post.List postInfo = posts.PostObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                    isNew = await m_DownloadHelper.DownloadPostDRMVideo(
                        user_agent: Auth.USER_AGENT,
                        policy: policy,
                        signature: signature,
                        kvp: kvp,
                        sess: Auth.COOKIE,
                        url: mpdURL,
                        decryptionKey: decryptionKey,
                        folder: path,
                        lastModified: lastModified,
                        media_id: postKVP.Key,
                        task: task,
                        filenameFormat: !string.IsNullOrEmpty(Config.PostFileNameFormat) ? Config.PostFileNameFormat : string.Empty,
                        postInfo: postInfo,
                        postMedia: mediaInfo,
                        author: postInfo?.author,
                        users: hasSelectedUsersKVP.Value,
                        config: Config,
                        showScrapeSize: Config.ShowScrapeSize);
                    if (isNew)
                    {
                        newPostCount++;
                    }
                    else
                    {
                        oldPostCount++;
                    }
                }
                else
                {
                    try
                    {
                        Post.Medium? mediaInfo = posts.PostMedia.FirstOrDefault(m => (m?.id == postKVP.Key) == true);
                        Post.List? postInfo = posts.PostObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                        isNew = await m_DownloadHelper.DownloadPostMedia(
                            url: postKVP.Value,
                            folder: path,
                            media_id: postKVP.Key,
                            task: task,
                            filenameFormat: !string.IsNullOrEmpty(Config.PostFileNameFormat) ? Config.PostFileNameFormat : string.Empty,
                            postInfo: postInfo,
                            postMedia: mediaInfo,
                            author: postInfo?.author,
                            users: hasSelectedUsersKVP.Value,
                            config: Config,
                            Config.ShowScrapeSize);
                        if (isNew)
                        {
                            newPostCount++;
                        }
                        else
                        {
                            oldPostCount++;
                        }
                    }
                    catch
                    {
                        Console.WriteLine("Media was null");
                    }
                }
            }
            task.StopTask();
        });
        AnsiConsole.Markup($"[red]Posts Already Downloaded: {oldPostCount} New Posts Downloaded: {newPostCount}[/]\n");

        return postCount;
    }

    private static async Task<int> DownloadPaidPosts(KeyValuePair<bool, Dictionary<string, int>> hasSelectedUsersKVP, KeyValuePair<string, int> user, int paidPostCount, string path)
    {
        AnsiConsole.Markup($"[red]Getting Paid Posts\n[/]");
        //Dictionary<long, string> purchasedPosts = await apiHelper.GetMedia(MediaType.PaidPosts, "/posts/paid", user.Key, path, auth, paid_post_ids);
        PaidPostCollection purchasedPosts = await m_ApiHelper.GetPaidPosts("/posts/paid", path, user.Key, Auth!, Config!, paid_post_ids);
        int oldPaidPostCount = 0;
        int newPaidPostCount = 0;
        if (purchasedPosts == null || purchasedPosts.PaidPosts.Count <= 0)
        {
            AnsiConsole.Markup($"[red]Found 0 Paid Posts\n[/]");
            return 0;
        }

        AnsiConsole.Markup($"[red]Found {purchasedPosts.PaidPosts.Count} Paid Posts\n[/]");
        paidPostCount = purchasedPosts.PaidPosts.Count;
        long totalSize = 0;
        if (Config.ShowScrapeSize)
        {
            totalSize = await m_DownloadHelper.CalculateTotalFileSize(purchasedPosts.PaidPosts.Values.ToList(), Auth);
        }
        else
        {
            totalSize = paidPostCount;
        }
        await AnsiConsole.Progress()
        .Columns(GetProgressColumns(Config.ShowScrapeSize))
        .StartAsync(async ctx =>
        {
            // Define tasks
            var task = ctx.AddTask($"[red]Downloading {purchasedPosts.PaidPosts.Count} Paid Posts[/]", autoStart: false);
            task.MaxValue = totalSize;
            task.StartTask();
            foreach (KeyValuePair<long, string> purchasedPostKVP in purchasedPosts.PaidPosts)
            {
                bool isNew;
                if (purchasedPostKVP.Value.Contains("cdn3.onlyfans.com/dash/files"))
                {
                    string[] messageUrlParsed = purchasedPostKVP.Value.Split(',');
                    string mpdURL = messageUrlParsed[0];
                    string policy = messageUrlParsed[1];
                    string signature = messageUrlParsed[2];
                    string kvp = messageUrlParsed[3];
                    string mediaId = messageUrlParsed[4];
                    string postId = messageUrlParsed[5];
                    string? licenseURL = null;
                    string? pssh = await m_ApiHelper.GetDRMMPDPSSH(mpdURL, policy, signature, kvp, Auth);
                    if (pssh == null)
                    {
                        continue;
                    }

                    DateTime lastModified = await m_ApiHelper.GetDRMMPDLastModified(mpdURL, policy, signature, kvp, Auth);
                    Dictionary<string, string> drmHeaders = await m_ApiHelper.GetDynamicHeaders($"/api2/v2/users/media/{mediaId}/drm/post/{postId}", "?type=widevine", Auth);
                    string decryptionKey;
                    if (clientIdBlobMissing || devicePrivateKeyMissing)
                    {
                        decryptionKey = await m_ApiHelper.GetDecryptionKey(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/post/{postId}?type=widevine", pssh, Auth);
                    }
                    else
                    {
                        decryptionKey = await m_ApiHelper.GetDecryptionKeyNew(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/post/{postId}?type=widevine", pssh, Auth);
                    }
                    Purchased.Medium? mediaInfo = purchasedPosts.PaidPostMedia.FirstOrDefault(m => m.id == purchasedPostKVP.Key);
                    Purchased.List? postInfo = purchasedPosts.PaidPostObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                    isNew = await m_DownloadHelper.DownloadPurchasedPostDRMVideo(
                        user_agent: Auth.USER_AGENT,
                        policy: policy,
                        signature: signature,
                        kvp: kvp,
                        sess: Auth.COOKIE,
                        url: mpdURL,
                        decryptionKey: decryptionKey,
                        folder: path,
                        lastModified: lastModified,
                        media_id: purchasedPostKVP.Key,
                        task: task,
                        filenameFormat: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? Config.PaidPostFileNameFormat : string.Empty,
                        postInfo: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? postInfo : null,
                        postMedia: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? mediaInfo : null,
                        fromUser: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? postInfo.fromUser : null,
                        users: hasSelectedUsersKVP.Value,
                        config: Config,
                        showScrapeSize: Config.ShowScrapeSize);
                    if (isNew)
                    {
                        newPaidPostCount++;
                    }
                    else
                    {
                        oldPaidPostCount++;
                    }
                }
                else
                {
                    Purchased.Medium mediaInfo = purchasedPosts.PaidPostMedia.FirstOrDefault(m => m.id == purchasedPostKVP.Key);
                    Purchased.List postInfo = purchasedPosts.PaidPostObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                    isNew = await m_DownloadHelper.DownloadPurchasedPostMedia(
                        url: purchasedPostKVP.Value,
                        folder: path,
                        media_id: purchasedPostKVP.Key,
                        task: task,
                        filenameFormat: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? Config.PaidPostFileNameFormat : string.Empty,
                        messageInfo: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? postInfo : null,
                        messageMedia: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? mediaInfo : null,
                        fromUser: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? postInfo.fromUser : null,
                        users: hasSelectedUsersKVP.Value,
                        config: Config,
                        Config.ShowScrapeSize);
                    if (isNew)
                    {
                        newPaidPostCount++;
                    }
                    else
                    {
                        oldPaidPostCount++;
                    }
                }
            }
            task.StopTask();
        });
        AnsiConsole.Markup($"[red]Paid Posts Already Downloaded: {oldPaidPostCount} New Paid Posts Downloaded: {newPaidPostCount}[/]\n");

        return paidPostCount;
    }

    private static async Task<int> DownloadPaidPostsPurchasedTab(PaidPostCollection purchasedPosts, KeyValuePair<string, int> user, int paidPostCount, string path, Dictionary<string, int> users)
    {
        int oldPaidPostCount = 0;
        int newPaidPostCount = 0;
        if (purchasedPosts == null || purchasedPosts.PaidPosts.Count <= 0)
        {
            AnsiConsole.Markup($"[red]Found 0 Paid Posts\n[/]");
            return 0;
        }

        AnsiConsole.Markup($"[red]Found {purchasedPosts.PaidPosts.Count} Paid Posts\n[/]");
        paidPostCount = purchasedPosts.PaidPosts.Count;
        long totalSize = 0;
        if (Config.ShowScrapeSize)
        {
            totalSize = await m_DownloadHelper.CalculateTotalFileSize(purchasedPosts.PaidPosts.Values.ToList(), Auth);
        }
        else
        {
            totalSize = paidPostCount;
        }
        await AnsiConsole.Progress()
        .Columns(GetProgressColumns(Config.ShowScrapeSize))
        .StartAsync(async ctx =>
        {
            // Define tasks
            var task = ctx.AddTask($"[red]Downloading {purchasedPosts.PaidPosts.Count} Paid Posts[/]", autoStart: false);
            task.MaxValue = totalSize;
            task.StartTask();
            foreach (KeyValuePair<long, string> purchasedPostKVP in purchasedPosts.PaidPosts)
            {
                bool isNew;
                if (purchasedPostKVP.Value.Contains("cdn3.onlyfans.com/dash/files"))
                {
                    string[] messageUrlParsed = purchasedPostKVP.Value.Split(',');
                    string mpdURL = messageUrlParsed[0];
                    string policy = messageUrlParsed[1];
                    string signature = messageUrlParsed[2];
                    string kvp = messageUrlParsed[3];
                    string mediaId = messageUrlParsed[4];
                    string postId = messageUrlParsed[5];
                    string? licenseURL = null;
                    string? pssh = await m_ApiHelper.GetDRMMPDPSSH(mpdURL, policy, signature, kvp, Auth);
                    if (pssh == null)
                    {
                        continue;
                    }

                    DateTime lastModified = await m_ApiHelper.GetDRMMPDLastModified(mpdURL, policy, signature, kvp, Auth);
                    Dictionary<string, string> drmHeaders = await m_ApiHelper.GetDynamicHeaders($"/api2/v2/users/media/{mediaId}/drm/post/{postId}", "?type=widevine", Auth);
                    string decryptionKey;
                    if (clientIdBlobMissing || devicePrivateKeyMissing)
                    {
                        decryptionKey = await m_ApiHelper.GetDecryptionKey(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/post/{postId}?type=widevine", pssh, Auth);
                    }
                    else
                    {
                        decryptionKey = await m_ApiHelper.GetDecryptionKeyNew(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/post/{postId}?type=widevine", pssh, Auth);
                    }
                    Purchased.Medium? mediaInfo = purchasedPosts.PaidPostMedia.FirstOrDefault(m => m.id == purchasedPostKVP.Key);
                    Purchased.List? postInfo = purchasedPosts.PaidPostObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                    isNew = await m_DownloadHelper.DownloadPurchasedPostDRMVideo(
                        user_agent: Auth.USER_AGENT,
                        policy: policy,
                        signature: signature,
                        kvp: kvp,
                        sess: Auth.COOKIE,
                        url: mpdURL,
                        decryptionKey: decryptionKey,
                        folder: path,
                        lastModified: lastModified,
                        media_id: purchasedPostKVP.Key,
                        task: task,
                        filenameFormat: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? Config.PaidPostFileNameFormat : string.Empty,
                        postInfo: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? postInfo : null,
                        postMedia: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? mediaInfo : null,
                        fromUser: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? postInfo.fromUser : null,
                        users: users,
                        config: Config,
                        showScrapeSize: Config.ShowScrapeSize);
                    if (isNew)
                    {
                        newPaidPostCount++;
                    }
                    else
                    {
                        oldPaidPostCount++;
                    }
                }
                else
                {
                    Purchased.Medium mediaInfo = purchasedPosts.PaidPostMedia.FirstOrDefault(m => m.id == purchasedPostKVP.Key);
                    Purchased.List postInfo = purchasedPosts.PaidPostObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                    isNew = await m_DownloadHelper.DownloadPurchasedPostMedia(
                        url: purchasedPostKVP.Value,
                        folder: path,
                        media_id: purchasedPostKVP.Key,
                        task: task,
                        filenameFormat: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? Config.PaidPostFileNameFormat : string.Empty,
                        messageInfo: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? postInfo : null,
                        messageMedia: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? mediaInfo : null,
                        fromUser: !string.IsNullOrEmpty(Config.PaidPostFileNameFormat) ? postInfo.fromUser : null,
                        users: users,
                        config: Config,
                        Config.ShowScrapeSize);
                    if (isNew)
                    {
                        newPaidPostCount++;
                    }
                    else
                    {
                        oldPaidPostCount++;
                    }
                }
            }
            task.StopTask();
        });
        AnsiConsole.Markup($"[red]Paid Posts Already Downloaded: {oldPaidPostCount} New Paid Posts Downloaded: {newPaidPostCount}[/]\n");

        return paidPostCount;
    }

    private static async Task<int> DownloadPaidMessagesPurchasedTab(PaidMessageCollection paidMessageCollection, KeyValuePair<string, int> user, int paidMessagesCount, string path, Dictionary<string, int> users)
    {
        int oldPaidMessagesCount = 0;
        int newPaidMessagesCount = 0;
        if (paidMessageCollection != null && paidMessageCollection.PaidMessages.Count > 0)
        {
            AnsiConsole.Markup($"[red]Found {paidMessageCollection.PaidMessages.Count} Paid Messages\n[/]");
            paidMessagesCount = paidMessageCollection.PaidMessages.Count;
            long totalSize = 0;
            if (Config.ShowScrapeSize)
            {
                totalSize = await m_DownloadHelper.CalculateTotalFileSize(paidMessageCollection.PaidMessages.Values.ToList(), Auth);
            }
            else
            {
                totalSize = paidMessagesCount;
            }
            await AnsiConsole.Progress()
            .Columns(GetProgressColumns(Config.ShowScrapeSize))
            .StartAsync(async ctx =>
            {
                // Define tasks
                var task = ctx.AddTask($"[red]Downloading {paidMessageCollection.PaidMessages.Count} Paid Messages[/]", autoStart: false);
                task.MaxValue = totalSize;
                task.StartTask();
                foreach (KeyValuePair<long, string> paidMessageKVP in paidMessageCollection.PaidMessages)
                {
                    bool isNew;
                    if (paidMessageKVP.Value.Contains("cdn3.onlyfans.com/dash/files"))
                    {
                        string[] messageUrlParsed = paidMessageKVP.Value.Split(',');
                        string mpdURL = messageUrlParsed[0];
                        string policy = messageUrlParsed[1];
                        string signature = messageUrlParsed[2];
                        string kvp = messageUrlParsed[3];
                        string mediaId = messageUrlParsed[4];
                        string messageId = messageUrlParsed[5];
                        string? licenseURL = null;
                        string? pssh = await m_ApiHelper.GetDRMMPDPSSH(mpdURL, policy, signature, kvp, Auth);
                        if (pssh != null)
                        {
                            DateTime lastModified = await m_ApiHelper.GetDRMMPDLastModified(mpdURL, policy, signature, kvp, Auth);
                            Dictionary<string, string> drmHeaders = await m_ApiHelper.GetDynamicHeaders($"/api2/v2/users/media/{mediaId}/drm/message/{messageId}", "?type=widevine", Auth);
                            string decryptionKey;
                            if (clientIdBlobMissing || devicePrivateKeyMissing)
                            {
                                decryptionKey = await m_ApiHelper.GetDecryptionKey(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/message/{messageId}?type=widevine", pssh, Auth);
                            }
                            else
                            {
                                decryptionKey = await m_ApiHelper.GetDecryptionKeyNew(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/message/{messageId}?type=widevine", pssh, Auth);
                            }


                            Purchased.Medium? mediaInfo = paidMessageCollection.PaidMessageMedia.FirstOrDefault(m => m.id == paidMessageKVP.Key);
                            Purchased.List? messageInfo = paidMessageCollection.PaidMessageObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                            isNew = await m_DownloadHelper.DownloadPurchasedMessageDRMVideo(
                                user_agent: Auth.USER_AGENT,
                                policy: policy,
                                signature: signature,
                                kvp: kvp,
                                sess: Auth.COOKIE,
                                url: mpdURL,
                                decryptionKey: decryptionKey,
                                folder: path,
                                lastModified: lastModified,
                                media_id: paidMessageKVP.Key,
                                task: task,
                                filenameFormat: !string.IsNullOrEmpty(Config.PaidMessageFileNameFormat) ? Config.PaidMessageFileNameFormat : string.Empty,
                                messageInfo: messageInfo,
                                messageMedia: mediaInfo,
                                fromUser: messageInfo.fromUser,
                                users: users,
                                config: Config,
                                showScrapeSize: Config.ShowScrapeSize);

                            if (isNew)
                            {
                                newPaidMessagesCount++;
                            }
                            else
                            {
                                oldPaidMessagesCount++;
                            }
                        }
                    }
                    else
                    {
                        Purchased.Medium? mediaInfo = paidMessageCollection.PaidMessageMedia.FirstOrDefault(m => m.id == paidMessageKVP.Key);
                        Purchased.List messageInfo = paidMessageCollection.PaidMessageObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                        isNew = await m_DownloadHelper.DownloadPurchasedMedia(
                            url: paidMessageKVP.Value,
                            folder: path,
                            media_id: paidMessageKVP.Key,
                            task: task,
                            filenameFormat: !string.IsNullOrEmpty(Config.PaidMessageFileNameFormat) ? Config.PaidMessageFileNameFormat : string.Empty,
                            messageInfo: messageInfo,
                            messageMedia: mediaInfo,
                            fromUser: messageInfo.fromUser,
                            users: users,
                            config: Config,
                            Config.ShowScrapeSize);
                        if (isNew)
                        {
                            newPaidMessagesCount++;
                        }
                        else
                        {
                            oldPaidMessagesCount++;
                        }
                    }
                }
                task.StopTask();
            });
            AnsiConsole.Markup($"[red]Paid Messages Already Downloaded: {oldPaidMessagesCount} New Paid Messages Downloaded: {newPaidMessagesCount}[/]\n");
        }
        else
        {
            AnsiConsole.Markup($"[red]Found 0 Paid Messages\n[/]");
        }

        return paidMessagesCount;
    }

    private static async Task<int> DownloadStreams(KeyValuePair<bool, Dictionary<string, int>> hasSelectedUsersKVP, KeyValuePair<string, int> user, int streamsCount, string path)
    {
        AnsiConsole.Markup($"[red]Getting Streams\n[/]");
        StreamsCollection streams = await m_ApiHelper.GetStreams($"/users/{user.Value}/posts/streams", path, Auth!, Config!, paid_post_ids);
        int oldStreamsCount = 0;
        int newStreamsCount = 0;
        if (streams == null || streams.Streams.Count <= 0)
        {
            AnsiConsole.Markup($"[red]Found 0 Streams\n[/]");
            return 0;
        }

        AnsiConsole.Markup($"[red]Found {streams.Streams.Count} Streams\n[/]");
        streamsCount = streams.Streams.Count;
        long totalSize = 0;
        if (Config.ShowScrapeSize)
        {
            totalSize = await m_DownloadHelper.CalculateTotalFileSize(streams.Streams.Values.ToList(), Auth);
        }
        else
        {
            totalSize = streamsCount;
        }
        await AnsiConsole.Progress()
        .Columns(GetProgressColumns(Config.ShowScrapeSize))
        .StartAsync(async ctx =>
        {
            var task = ctx.AddTask($"[red]Downloading {streams.Streams.Count} Streams[/]", autoStart: false);
            task.MaxValue = totalSize;
            task.StartTask();
            foreach (KeyValuePair<long, string> streamKVP in streams.Streams)
            {
                bool isNew;
                if (streamKVP.Value.Contains("cdn3.onlyfans.com/dash/files"))
                {
                    string[] messageUrlParsed = streamKVP.Value.Split(',');
                    string mpdURL = messageUrlParsed[0];
                    string policy = messageUrlParsed[1];
                    string signature = messageUrlParsed[2];
                    string kvp = messageUrlParsed[3];
                    string mediaId = messageUrlParsed[4];
                    string postId = messageUrlParsed[5];
                    string? licenseURL = null;
                    string? pssh = await m_ApiHelper.GetDRMMPDPSSH(mpdURL, policy, signature, kvp, Auth);
                    if (pssh == null)
                    {
                        continue;
                    }

                    DateTime lastModified = await m_ApiHelper.GetDRMMPDLastModified(mpdURL, policy, signature, kvp, Auth);
                    Dictionary<string, string> drmHeaders = await m_ApiHelper.GetDynamicHeaders($"/api2/v2/users/media/{mediaId}/drm/post/{postId}", "?type=widevine", Auth);
                    string decryptionKey;
                    if (clientIdBlobMissing || devicePrivateKeyMissing)
                    {
                        decryptionKey = await m_ApiHelper.GetDecryptionKey(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/post/{postId}?type=widevine", pssh, Auth);
                    }
                    else
                    {
                        decryptionKey = await m_ApiHelper.GetDecryptionKeyNew(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/post/{postId}?type=widevine", pssh, Auth);
                    }
                    Streams.Medium mediaInfo = streams.StreamMedia.FirstOrDefault(m => m.id == streamKVP.Key);
                    Streams.List streamInfo = streams.StreamObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                    isNew = await m_DownloadHelper.DownloadStreamsDRMVideo(
                        user_agent: Auth.USER_AGENT,
                        policy: policy,
                        signature: signature,
                        kvp: kvp,
                        sess: Auth.COOKIE,
                        url: mpdURL,
                        decryptionKey: decryptionKey,
                        folder: path,
                        lastModified: lastModified,
                        media_id: streamKVP.Key,
                        task: task,
                        filenameFormat: !string.IsNullOrEmpty(Config.PostFileNameFormat) ? Config.PostFileNameFormat : string.Empty,
                        streamInfo: streamInfo,
                        streamMedia: mediaInfo,
                        author: streamInfo?.author,
                        users: hasSelectedUsersKVP.Value,
                        config: Config,
                        showScrapeSize: Config.ShowScrapeSize);
                    if (isNew)
                    {
                        newStreamsCount++;
                    }
                    else
                    {
                        oldStreamsCount++;
                    }
                }
                else
                {
                    try
                    {
                        Streams.Medium? mediaInfo = streams.StreamMedia.FirstOrDefault(m => (m?.id == streamKVP.Key) == true);
                        Streams.List? streamInfo = streams.StreamObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                        isNew = await m_DownloadHelper.DownloadStreamMedia(
                            url: streamKVP.Value,
                            folder: path,
                            media_id: streamKVP.Key,
                            task: task,
                            filenameFormat: !string.IsNullOrEmpty(Config.PostFileNameFormat) ? Config.PostFileNameFormat : string.Empty,
                            streamInfo: streamInfo,
                            streamMedia: mediaInfo,
                            author: streamInfo?.author,
                            users: hasSelectedUsersKVP.Value,
                            config: Config,
                            Config.ShowScrapeSize);
                        if (isNew)
                        {
                            newStreamsCount++;
                        }
                        else
                        {
                            oldStreamsCount++;
                        }
                    }
                    catch
                    {
                        Console.WriteLine("Media was null");
                    }
                }
            }
            task.StopTask();
        });
        AnsiConsole.Markup($"[red]Streams Already Downloaded: {oldStreamsCount} New Streams Downloaded: {newStreamsCount}[/]\n");

        return streamsCount;
    }

    private static async Task DownloadSinglePost(long post_id, string path, Dictionary<string, int> users)
    {
        AnsiConsole.Markup($"[red]Getting Post\n[/]");
        SinglePostCollection post = await m_ApiHelper.GetPost($"/posts/{post_id.ToString()}", path, Auth!, Config!);

        if (post == null)
        {
            AnsiConsole.Markup($"[red]Couldn't find post\n[/]");
            return;
        }

        long totalSize = 0;
        if (Config.ShowScrapeSize)
        {
            totalSize = await m_DownloadHelper.CalculateTotalFileSize(post.SinglePosts.Values.ToList(), Auth);
        }
        else
        {
            totalSize = post.SinglePosts.Count;
        }
        bool isNew = false;
        await AnsiConsole.Progress()
        .Columns(GetProgressColumns(Config.ShowScrapeSize))
        .StartAsync(async ctx =>
        {
            var task = ctx.AddTask($"[red]Downloading Post[/]", autoStart: false);
            task.MaxValue = totalSize;
            task.StartTask();
            foreach (KeyValuePair<long, string> postKVP in post.SinglePosts)
            {
                if (postKVP.Value.Contains("cdn3.onlyfans.com/dash/files"))
                {
                    string[] messageUrlParsed = postKVP.Value.Split(',');
                    string mpdURL = messageUrlParsed[0];
                    string policy = messageUrlParsed[1];
                    string signature = messageUrlParsed[2];
                    string kvp = messageUrlParsed[3];
                    string mediaId = messageUrlParsed[4];
                    string postId = messageUrlParsed[5];
                    string? licenseURL = null;
                    string? pssh = await m_ApiHelper.GetDRMMPDPSSH(mpdURL, policy, signature, kvp, Auth);
                    if (pssh == null)
                    {
                        continue;
                    }

                    DateTime lastModified = await m_ApiHelper.GetDRMMPDLastModified(mpdURL, policy, signature, kvp, Auth);
                    Dictionary<string, string> drmHeaders = await m_ApiHelper.GetDynamicHeaders($"/api2/v2/users/media/{mediaId}/drm/post/{postId}", "?type=widevine", Auth);
                    string decryptionKey;
                    if (clientIdBlobMissing || devicePrivateKeyMissing)
                    {
                        decryptionKey = await m_ApiHelper.GetDecryptionKey(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/post/{postId}?type=widevine", pssh, Auth);
                    }
                    else
                    {
                        decryptionKey = await m_ApiHelper.GetDecryptionKeyNew(drmHeaders, $"https://onlyfans.com/api2/v2/users/media/{mediaId}/drm/post/{postId}?type=widevine", pssh, Auth);
                    }
                    SinglePost.Medium mediaInfo = post.SinglePostMedia.FirstOrDefault(m => m.id == postKVP.Key);
                    SinglePost postInfo = post.SinglePostObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                    isNew = await m_DownloadHelper.DownloadPostDRMVideo(
                        user_agent: Auth.USER_AGENT,
                        policy: policy,
                        signature: signature,
                        kvp: kvp,
                        sess: Auth.COOKIE,
                        url: mpdURL,
                        decryptionKey: decryptionKey,
                        folder: path,
                        lastModified: lastModified,
                        media_id: postKVP.Key,
                        task: task,
                        filenameFormat: !string.IsNullOrEmpty(Config.PostFileNameFormat) ? Config.PostFileNameFormat : string.Empty,
                        postInfo: postInfo,
                        postMedia: mediaInfo,
                        author: postInfo?.author,
                        users: users,
                        config: Config,
                        showScrapeSize: Config.ShowScrapeSize);
                }
                else
                {
                    try
                    {
                        SinglePost.Medium? mediaInfo = post.SinglePostMedia.FirstOrDefault(m => (m?.id == postKVP.Key) == true);
                        SinglePost? postInfo = post.SinglePostObjects.FirstOrDefault(p => p?.media?.Contains(mediaInfo) == true);

                        isNew = await m_DownloadHelper.DownloadPostMedia(
                            url: postKVP.Value,
                            folder: path,
                            media_id: postKVP.Key,
                            task: task,
                            filenameFormat: !string.IsNullOrEmpty(Config.PostFileNameFormat) ? Config.PostFileNameFormat : string.Empty,
                            postInfo: postInfo,
                            postMedia: mediaInfo,
                            author: postInfo?.author,
                            users: users,
                            config: Config,
                            Config.ShowScrapeSize);
                    }
                    catch
                    {
                        Console.WriteLine("Media was null");
                    }
                }
            }
            task.StopTask();
        });
        if (isNew)
        {
            AnsiConsole.Markup($"[red]Post {post_id} downloaded\n[/]");
        }
        else
        {
            AnsiConsole.Markup($"[red]Post {post_id} already downloaded\n[/]");
        }
    }
    public static async Task<KeyValuePair<bool, Dictionary<string, int>>> HandleUserSelection(Dictionary<string, int> selectedUsers, Dictionary<string, int> users, Dictionary<string, int> lists)
    {
        bool hasSelectedUsers = false;

        while (!hasSelectedUsers)
        {
            var mainMenuOptions = GetMainMenuOptions(users, lists);

            var mainMenuSelection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[red]Select Accounts to Scrape | Select All = All Accounts | List = Download content from users on List | Custom = Specific Account(s)[/]")
                    .AddChoices(mainMenuOptions)
            );

            switch (mainMenuSelection)
            {
                case "[red]Select All[/]":
                    selectedUsers = users;
                    hasSelectedUsers = true;
                    break;
                case "[red]List[/]":
                    while (true)
                    {
                        var listSelectionPrompt = new MultiSelectionPrompt<string>();
                        listSelectionPrompt.Title = "[red]Select List[/]";
                        listSelectionPrompt.PageSize = 10;
                        listSelectionPrompt.AddChoice("[red]Go Back[/]");
                        foreach (string key in lists.Keys.Select(k => $"[red]{k}[/]").ToList())
                        {
                            listSelectionPrompt.AddChoice(key);
                        }
                        var listSelection = AnsiConsole.Prompt(listSelectionPrompt);

                        if (listSelection.Contains("[red]Go Back[/]"))
                        {
                            break; // Go back to the main menu
                        }
                        else
                        {
                            hasSelectedUsers = true;
                            List<string> listUsernames = new();
                            foreach (var item in listSelection)
                            {
                                int listId = lists[item.Replace("[red]", "").Replace("[/]", "")];
                                List<string> usernames = await m_ApiHelper.GetListUsers($"/lists/{listId}/users", Auth);
                                foreach (string user in usernames)
                                {
                                    listUsernames.Add(user);
                                }
                            }
                            selectedUsers = users.Where(x => listUsernames.Contains($"{x.Key}")).Distinct().ToDictionary(x => x.Key, x => x.Value);
                            AnsiConsole.Markup(string.Format("[red]Downloading from List(s): {0}[/]", string.Join(", ", listSelection)));
                            break;
                        }
                    }
                    break;
                case "[red]Custom[/]":
                    while (true)
                    {
                        var selectedNamesPrompt = new MultiSelectionPrompt<string>();
                        selectedNamesPrompt.MoreChoicesText("[grey](Move up and down to reveal more choices)[/]");
                        selectedNamesPrompt.InstructionsText("[grey](Press <space> to select, <enter> to accept)[/]\n[grey](Press A-Z to easily navigate the list)[/]");
                        selectedNamesPrompt.Title("[red]Select users[/]");
                        selectedNamesPrompt.PageSize(10);
                        selectedNamesPrompt.AddChoice("[red]Go Back[/]");
                        foreach (string key in users.Keys.OrderBy(k => k).Select(k => $"[red]{k}[/]").ToList())
                        {
                            selectedNamesPrompt.AddChoice(key);
                        }
                        var userSelection = AnsiConsole.Prompt(selectedNamesPrompt);
                        if (userSelection.Contains("[red]Go Back[/]"))
                        {
                            break; // Go back to the main menu
                        }
                        else
                        {
                            hasSelectedUsers = true;
                            selectedUsers = users.Where(x => userSelection.Contains($"[red]{x.Key}[/]")).ToDictionary(x => x.Key, x => x.Value);
                            break;
                        }
                    }
                    break;
                case "[red]Download Single Post[/]":
                    return new KeyValuePair<bool, Dictionary<string, int>>(true, new Dictionary<string, int> { { "SinglePost", 0 } });
                case "[red]Download Purchased Tab[/]":
                    return new KeyValuePair<bool, Dictionary<string, int>>(true, new Dictionary<string, int> { { "PurchasedTab", 0 } });
                case "[red]Edit config.json[/]":
                    while (true)
                    {
                        var choices = new List<(string choice, bool isSelected)>();
                        choices.AddRange(new[]
                        {
                            ( "[red]Go Back[/]", false ),
                            ( "[red]DownloadAvatarHeaderPhoto[/]", Config.DownloadAvatarHeaderPhoto),
                            ( "[red]DownloadPaidPosts[/]", Config.DownloadPaidPosts ),
                            ( "[red]DownloadPosts[/]",  Config.DownloadPosts ),
                            ( "[red]DownloadArchived[/]", Config.DownloadArchived ),
                            ( "[red]DownloadStreams[/]", Config.DownloadStreams),
                            ( "[red]DownloadStories[/]", Config.DownloadStories ),
                            ( "[red]DownloadHighlights[/]", Config.DownloadHighlights ),
                            ( "[red]DownloadMessages[/]", Config.DownloadMessages ),
                            ( "[red]DownloadPaidMessages[/]", Config.DownloadPaidMessages ),
                            ( "[red]DownloadImages[/]", Config.DownloadImages ),
                            ( "[red]DownloadVideos[/]", Config.DownloadVideos ),
                            ( "[red]DownloadAudios[/]", Config.DownloadAudios ),
                            ( "[red]IncludeExpiredSubscriptions[/]", Config.IncludeExpiredSubscriptions ),
                            ( "[red]IncludeRestrictedSubscriptions[/]", Config.IncludeRestrictedSubscriptions ),
                            ( "[red]SkipAds[/]", Config.SkipAds ),
                            ( "[red]FolderPerPaidPost[/]", Config.FolderPerPaidPost ),
                            ( "[red]FolderPerPost[/]", Config.FolderPerPost ),
                            ( "[red]FolderPerPaidMessage[/]", Config.FolderPerPaidMessage ),
                            ( "[red]FolderPerMessage[/]", Config.FolderPerMessage ),
                            ( "[red]LimitDownloadRate[/]", Config.LimitDownloadRate ),
                            ( "[red]RenameExistingFilesOnCustomFormat[/]", Config.RenameExistingFilesWhenCustomFormatIsSelected ),
                            ( "[red]DownloadPostsBeforeOrAfterSpecificDate[/]", Config.DownloadOnlySpecificDates ),
                            ( "[red]ShowScrapeSize[/]", Config.ShowScrapeSize),
                            ( "[red]DownloadPostsIncrementally[/]", Config.DownloadPostsIncrementally),
                            ( "[red]NonInteractiveMode[/]", Config.NonInteractiveMode),
                            ( "[red]NonInteractiveModePurchasedTab[/]", Config.NonInteractiveModePurchasedTab)
                        });

                        MultiSelectionPrompt<string> multiSelectionPrompt = new MultiSelectionPrompt<string>()
                            .Title("[red]Edit config.json[/]")
                            .PageSize(25);

                        foreach (var choice in choices)
                        {
                            multiSelectionPrompt.AddChoices(choice.choice, (selectionItem) => { if (choice.isSelected) selectionItem.Select(); });
                        }

                        var configOptions = AnsiConsole.Prompt(multiSelectionPrompt);

                        if (configOptions.Contains("[red]Go Back[/]"))
                        {
                            break;
                        }

                        Config newConfig = new()
                        {
                            DownloadPath = Config.DownloadPath,
                            PostFileNameFormat = Config.PostFileNameFormat,
                            MessageFileNameFormat = Config.MessageFileNameFormat,
                            PaidPostFileNameFormat = Config.PaidPostFileNameFormat,
                            PaidMessageFileNameFormat = Config.PaidMessageFileNameFormat,
                            DownloadLimitInMbPerSec = Config.DownloadLimitInMbPerSec,
                            DownloadDateSelection = Config.DownloadDateSelection,
                            CustomDate = Config.CustomDate,
                            Timeout = Config.Timeout,
                            FFmpegPath = Config.FFmpegPath,
                            NonInteractiveModeListName = Config.NonInteractiveModeListName,
                            DownloadAvatarHeaderPhoto = configOptions.Contains("[red]DownloadAvatarHeaderPhoto[/]"),
                            DownloadPaidPosts = configOptions.Contains("[red]DownloadPaidPosts[/]"),
                            DownloadPosts = configOptions.Contains("[red]DownloadPosts[/]"),
                            DownloadArchived = configOptions.Contains("[red]DownloadArchived[/]"),
                            DownloadStreams = configOptions.Contains("[red]DownloadStreams[/]"),
                            DownloadStories = configOptions.Contains("[red]DownloadStories[/]"),
                            DownloadHighlights = configOptions.Contains("[red]DownloadHighlights[/]"),
                            DownloadMessages = configOptions.Contains("[red]DownloadMessages[/]"),
                            DownloadPaidMessages = configOptions.Contains("[red]DownloadPaidMessages[/]"),
                            DownloadImages = configOptions.Contains("[red]DownloadImages[/]"),
                            DownloadVideos = configOptions.Contains("[red]DownloadVideos[/]"),
                            DownloadAudios = configOptions.Contains("[red]DownloadAudios[/]"),
                            IncludeExpiredSubscriptions = configOptions.Contains("[red]IncludeExpiredSubscriptions[/]"),
                            IncludeRestrictedSubscriptions = configOptions.Contains("[red]IncludeRestrictedSubscriptions[/]"),
                            SkipAds = configOptions.Contains("[red]SkipAds[/]"),
                            FolderPerPaidPost = configOptions.Contains("[red]FolderPerPaidPost[/]"),
                            FolderPerPost = configOptions.Contains("[red]FolderPerPost[/]"),
                            FolderPerPaidMessage = configOptions.Contains("[red]FolderPerPaidMessage[/]"),
                            FolderPerMessage = configOptions.Contains("[red]FolderPerMessage[/]"),
                            LimitDownloadRate = configOptions.Contains("[red]LimitDownloadRate[/]"),
                            RenameExistingFilesWhenCustomFormatIsSelected = configOptions.Contains("[red]RenameExistingFilesOnCustomFormat[/]"),
                            DownloadOnlySpecificDates = configOptions.Contains("[red]DownloadPostsBeforeOrAfterSpecificDate[/]"),
                            ShowScrapeSize = configOptions.Contains("[red]ShowScrapeSize[/]"),
                            DownloadPostsIncrementally = configOptions.Contains("[red]DownloadPostsIncrementally[/]"),
                            NonInteractiveMode = configOptions.Contains("[red]NonInteractiveMode[/]"),
                            NonInteractiveModePurchasedTab = configOptions.Contains("[red]NonInteractiveModePurchasedTab[/]")
                        };


                        string newConfigString = JsonConvert.SerializeObject(newConfig, Formatting.Indented);
                        File.WriteAllText("config.json", newConfigString);
                        if (Config.IncludeExpiredSubscriptions != Config.IncludeExpiredSubscriptions)
                        {
                            Config = JsonConvert.DeserializeObject<Config>(File.ReadAllText("config.json"));
                            return new KeyValuePair<bool, Dictionary<string, int>>(true, new Dictionary<string, int> { { "ConfigChanged", 0 } });
                        }
                        Config = JsonConvert.DeserializeObject<Config>(File.ReadAllText("config.json"));
                        break;
                    }
                    break;
                case "[red]Exit[/]":
                    return new KeyValuePair<bool, Dictionary<string, int>>(false, null); // Return false to indicate exit
            }
        }

        return new KeyValuePair<bool, Dictionary<string, int>>(true, selectedUsers); // Return true to indicate selected users
    }


    public static List<string> GetMainMenuOptions(Dictionary<string, int> users, Dictionary<string, int> lists)
    {
        if (lists.Count > 0)
        {
            return new List<string>
            {
                "[red]Select All[/]",
                "[red]List[/]",
                "[red]Custom[/]",
                "[red]Download Single Post[/]",
                "[red]Download Purchased Tab[/]",
                "[red]Edit config.json[/]",
                "[red]Exit[/]"
            };
        }
        else
        {
            return new List<string>
            {
                "[red]Select All[/]",
                "[red]Custom[/]",
                "[red]Download Single Post[/]",
                "[red]Download Purchased Tab[/]",
                "[red]Edit config.json[/]",
                "[red]Exit[/]"
            };
        }
    }

    static bool ValidateFilePath(string path)
    {
        char[] invalidChars = System.IO.Path.GetInvalidPathChars();
        char[] foundInvalidChars = path.Where(c => invalidChars.Contains(c)).ToArray();

        if (foundInvalidChars.Any())
        {
            AnsiConsole.Markup($"[red]Invalid characters found in path {path}:[/] {string.Join(", ", foundInvalidChars)}\n");
            return false;
        }

        if (!System.IO.File.Exists(path))
        {
            if (System.IO.Directory.Exists(path))
            {
                AnsiConsole.Markup($"[red]The provided path {path} improperly points to a directory and not a file.[/]\n");
            }
            else
            {
                AnsiConsole.Markup($"[red]The provided path {path} does not exist or is not accessible.[/]\n");
            }
            return false;
        }

        return true;
    }
    static ProgressColumn[] GetProgressColumns(bool showScrapeSize)
    {
        List<ProgressColumn> progressColumns;
        if (showScrapeSize)
        {
            progressColumns = new List<ProgressColumn>()
            {
                new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new DownloadedColumn(), new RemainingTimeColumn()
            };
        }
        else
        {
            progressColumns = new List<ProgressColumn>()
            {
                new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn()
            };
        }
        return progressColumns.ToArray();
    }
}

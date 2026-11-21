namespace PcAnalyzer.Windows.Games;

/// <summary>Offline knowledge used by game detection: known games, launcher libraries and non-game apps.</summary>
public static class GameCatalog
{
    /// <summary>Folders used by game launchers. A process running from here is very likely a game.</summary>
    public static readonly string[] LibraryMarkers =
    {
        @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\gog games\", @"\xboxgames\",
        @"\windowsapps\", @"\riot games\", @"\ea games\", @"\electronic arts\", @"\ubisoft game launcher\games\",
        @"\ubisoft\", @"\battle.net\", @"\blizzard\", @"\rockstar games\", @"\wargaming.net\", @"\games\",
        @"\lesta\", @"\mygames\", @"\vk play\", @"\itch\apps\", @"\amazon games\library\",
    };

    /// <summary>Well-known game executables (lowercase, without .exe) → display name.</summary>
    public static readonly Dictionary<string, string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs2"] = "Counter-Strike 2", ["dota2"] = "Dota 2", ["valorant-win64-shipping"] = "Valorant",
        ["fortniteclient-win64-shipping"] = "Fortnite", ["r5apex"] = "Apex Legends", ["r5apex_dx12"] = "Apex Legends",
        ["gta5"] = "Grand Theft Auto V", ["gta5_enhanced"] = "Grand Theft Auto V Enhanced", ["rdr2"] = "Red Dead Redemption 2",
        ["cyberpunk2077"] = "Cyberpunk 2077", ["witcher3"] = "The Witcher 3", ["eldenring"] = "Elden Ring",
        ["bg3"] = "Baldur's Gate 3", ["bg3_dx11"] = "Baldur's Gate 3", ["starfield"] = "Starfield",
        ["hogwartslegacy"] = "Hogwarts Legacy", ["overwatch"] = "Overwatch 2", ["rocketleague"] = "Rocket League",
        ["pubg"] = "PUBG", ["tslgame"] = "PUBG", ["worldoftanks"] = "World of Tanks", ["tanki"] = "Мир танков",
        ["wows"] = "World of Warships", ["warthunder"] = "War Thunder", ["aces"] = "War Thunder",
        ["minecraft"] = "Minecraft", ["javaw"] = "Minecraft (Java)", ["robloxplayerbeta"] = "Roblox",
        ["leagueoflegends"] = "League of Legends", ["league of legends"] = "League of Legends",
        ["rainbowsix"] = "Rainbow Six Siege", ["rainbowsix_vulkan"] = "Rainbow Six Siege",
        ["destiny2"] = "Destiny 2", ["helldivers2"] = "Helldivers 2", ["palworld-win64-shipping"] = "Palworld",
        ["rustclient"] = "Rust", ["escapefromtarkov"] = "Escape from Tarkov", ["deadbydaylight-win64-shipping"] = "Dead by Daylight",
        ["forzahorizon5"] = "Forza Horizon 5", ["forzahorizon4"] = "Forza Horizon 4", ["acc"] = "Assetto Corsa Competizione",
        ["f1_24"] = "F1 24", ["f1_23"] = "F1 23", ["eafc25"] = "EA SPORTS FC 25", ["fc25"] = "EA SPORTS FC 25",
        ["bf2042"] = "Battlefield 2042", ["cod"] = "Call of Duty", ["cod24-cod"] = "Call of Duty",
        ["stalker2-win64-shipping"] = "S.T.A.L.K.E.R. 2", ["blackmythwukong"] = "Black Myth: Wukong",
        ["b1-win64-shipping"] = "Black Myth: Wukong", ["marvel-win64-shipping"] = "Marvel Rivals",
        ["deltaforceclient-win64-shipping"] = "Delta Force", ["thefinals"] = "The Finals",
        ["diablo iv"] = "Diablo IV", ["wow"] = "World of Warcraft", ["sc2"] = "StarCraft II",
        ["hl2"] = "Half-Life 2", ["re4"] = "Resident Evil 4", ["monsterhunterwilds"] = "Monster Hunter Wilds",
        ["pathofexile"] = "Path of Exile", ["pathofexile_x64steam"] = "Path of Exile", ["pathofexile2"] = "Path of Exile 2",
        ["genshinimpact"] = "Genshin Impact", ["zenlesszonezero"] = "Zenless Zone Zero", ["starrail"] = "Honkai: Star Rail",
        ["once_human"] = "Once Human", ["msfs"] = "Microsoft Flight Simulator", ["flightsimulator"] = "Microsoft Flight Simulator", ["eurotrucks2"] = "Euro Truck Simulator 2",
    };

    /// <summary>Applications that are never treated as games, even fullscreen with GPU load.</summary>
    public static readonly HashSet<string> NonGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "chrome", "msedge", "msedgewebview2", "firefox", "opera", "opera_gx", "brave", "vivaldi", "browser", "yandex", "arc",
        "discord", "telegram", "whatsapp", "slack", "teams", "ms-teams", "zoom", "skype", "spotify",
        "steam", "steamwebhelper", "epicgameslauncher", "epicwebhelper", "galaxyclient", "eadesktop", "eabackgroundservice", "upc",
        "ubisoftconnect", "riotclientservices", "riotclientux", "battle.net", "agent", "lgc", "vkplay", "gamecenter",
        "obs64", "obs32", "obs", "streamlabs", "nvidia share", "nvcontainer", "nvidia overlay", "radeonsoftware", "amdrsserv",
        "msiafterburner", "rtss", "vlc", "mpc-hc64", "mpc-hc", "mpc-be64", "potplayermini64", "potplayer", "wmplayer", "video.ui", "mpv",
        "devenv", "code", "rider64", "idea64", "pycharm64", "notepad", "notepad++", "windowsterminal", "cmd", "powershell", "pwsh", "conhost",
        "taskmgr", "mmc", "applicationframehost", "searchhost", "shellexperiencehost", "startmenuexperiencehost", "textinputhost", "lockapp",
        "photoshop", "illustrator", "afterfx", "adobe premiere pro", "resolve", "blender", "unity", "unityhub", "unrealeditor",
        "wallpaper32", "wallpaper64", "wallpaperengine", "lively", "claude", "pcanalyzer.app", "presentmon", "olk", "outlook", "winword", "excel", "powerpnt",
    };
}

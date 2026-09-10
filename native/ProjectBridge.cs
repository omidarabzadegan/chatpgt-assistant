using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ChatGptAssistant.ProjectBridge
{
    internal sealed class BridgeConfig
    {
        public string projectRoot { get; set; }
        public string workspaceFile { get; set; }
        public string dataDirectory { get; set; }
        public int maxReadBytes { get; set; }
        public int maxSearchResults { get; set; }
        public int maxContextChars { get; set; }
        public int processTimeoutSeconds { get; set; }
        public TestProfile[] tests { get; set; }
        public bool executionEnabled { get; set; }
        public string[] allowedLocalCommands { get; set; }
        public string[] allowedLocalScripts { get; set; }
        public ServerProfile[] servers { get; set; }
        public ProjectServerBinding[] projectServers { get; set; }
    }

    internal sealed class TestProfile
    {
        public string id { get; set; }
        public string fileName { get; set; }
        public string arguments { get; set; }
        public string workingDirectory { get; set; }
        public string root { get; set; }
        public int timeoutSeconds { get; set; }
    }

    internal sealed class ServerProfile
    {
        public string id { get; set; }
        public string host { get; set; }
        public string user { get; set; }
        public int port { get; set; }
        public string identityFile { get; set; }
        public string workingDirectory { get; set; }
        public string[] allowedCommands { get; set; }
    }

    internal sealed class ProjectServerBinding
    {
        public string projectPath { get; set; }
        public string projectName { get; set; }
        public string server { get; set; }
    }

    internal sealed class WorkspaceFileModel
    {
        public WorkspaceFolderModel[] folders { get; set; }
    }

    internal sealed class WorkspaceFolderModel
    {
        public string name { get; set; }
        public string path { get; set; }
    }

    internal sealed class WorkspaceRoot
    {
        public string id { get; set; }
        public string name { get; set; }
        public string path { get; set; }
        public string pathWithSeparator { get; set; }
        public bool primary { get; set; }
    }

    internal sealed class BackupEntry
    {
        public string root { get; set; }
        public string path { get; set; }
        public bool existed { get; set; }
        public bool isDirectory { get; set; }
    }

    internal sealed class ProcessResult
    {
        public int ExitCode;
        public string Output;
        public string Error;
        public bool TimedOut;
    }

    internal static class Program
    {
        private const int MaxInboundMessageBytes = 4 * 1024 * 1024;
        private const int MaxOutboundMessageBytes = 900 * 1024;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = MaxInboundMessageBytes };

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                string configPath = ResolveConfigPath(args);
                BridgeConfig config = LoadConfig(configPath);
                Stream input = Console.OpenStandardInput();
                Stream output = Console.OpenStandardOutput();

                while (true)
                {
                    byte[] lengthBytes = ReadExact(input, 4);
                    if (lengthBytes == null)
                        break;

                    int length = BitConverter.ToInt32(lengthBytes, 0);
                    if (length <= 0 || length > MaxInboundMessageBytes)
                        throw new InvalidDataException("Native message length is invalid.");

                    byte[] payload = ReadExact(input, length);
                    if (payload == null)
                        throw new EndOfStreamException("Native message ended unexpectedly.");

                    string requestJson = new UTF8Encoding(false, true).GetString(payload);
                    Dictionary<string, object> request = Json.Deserialize<Dictionary<string, object>>(requestJson);
                    Dictionary<string, object> response;
                    try
                    {
                        string projectRootOverride = GetRequestedProjectRoot(request);
                        Bridge bridge = new Bridge(config, projectRootOverride, configPath);
                        response = bridge.Handle(request);
                    }
                    catch (Exception ex)
                    {
                        response = new Dictionary<string, object>
                        {
                            { "ok", false },
                            { "error", SafeMessage(ex) }
                        };
                    }

                    byte[] responseBytes = Encoding.UTF8.GetBytes(Json.Serialize(response));
                    if (responseBytes.Length > MaxOutboundMessageBytes)
                    {
                        responseBytes = Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object>
                        {
                            { "ok", false },
                            { "error", "Bridge response exceeded the Chrome Native Messaging limit." }
                        }));
                    }

                    byte[] responseLength = BitConverter.GetBytes(responseBytes.Length);
                    output.Write(responseLength, 0, responseLength.Length);
                    output.Write(responseBytes, 0, responseBytes.Length);
                    output.Flush();
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(SafeMessage(ex));
                return 1;
            }
        }

        private static string ResolveConfigPath(string[] args)
        {
            foreach (string arg in args ?? new string[0])
            {
                if (arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(arg.Substring("--config=".Length).Trim('"'));
            }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        }

        private static BridgeConfig LoadConfig(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Bridge configuration was not found.", path);

            BridgeConfig config = Json.Deserialize<BridgeConfig>(File.ReadAllText(path, Encoding.UTF8));
            if (config == null || String.IsNullOrWhiteSpace(config.projectRoot))
                throw new InvalidDataException("projectRoot is required in config.json.");

            config.projectRoot = Path.GetFullPath(config.projectRoot.Trim());
            if (!Directory.Exists(config.projectRoot))
                throw new DirectoryNotFoundException("Configured project root does not exist: " + config.projectRoot);

            if (!String.IsNullOrWhiteSpace(config.workspaceFile))
            {
                config.workspaceFile = Path.GetFullPath(config.workspaceFile.Trim());
                if (!File.Exists(config.workspaceFile))
                    throw new FileNotFoundException("Configured VS Code workspace file does not exist.", config.workspaceFile);
            }

            if (String.IsNullOrWhiteSpace(config.dataDirectory))
                config.dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            config.dataDirectory = Path.GetFullPath(config.dataDirectory);
            Directory.CreateDirectory(config.dataDirectory);

            if (config.maxReadBytes <= 0) config.maxReadBytes = 256 * 1024;
            if (config.maxSearchResults <= 0) config.maxSearchResults = 80;
            if (config.maxContextChars <= 0) config.maxContextChars = 60000;
            if (config.processTimeoutSeconds <= 0) config.processTimeoutSeconds = 20;
            if (config.tests == null) config.tests = new TestProfile[0];
            if (config.allowedLocalCommands == null) config.allowedLocalCommands = new string[0];
            if (config.allowedLocalScripts == null) config.allowedLocalScripts = new string[0];
            if (config.servers == null) config.servers = new ServerProfile[0];
            if (config.projectServers == null) config.projectServers = new ProjectServerBinding[0];

            foreach (ServerProfile server in config.servers)
            {
                if (server == null || String.IsNullOrWhiteSpace(server.id))
                    throw new InvalidDataException("Each server profile requires an id.");
                if (!Regex.IsMatch(server.id, "^[A-Za-z0-9_-]{1,80}$"))
                    throw new InvalidDataException("Server profile id is invalid: " + server.id);
                if (String.IsNullOrWhiteSpace(server.host) || server.host.Any(Char.IsWhiteSpace))
                    throw new InvalidDataException("Server host is invalid: " + server.id);
                if (String.IsNullOrWhiteSpace(server.user) || !Regex.IsMatch(server.user, "^[A-Za-z0-9._-]{1,80}$"))
                    throw new InvalidDataException("Server user is invalid: " + server.id);
                if (server.port <= 0 || server.port > 65535) server.port = 22;
                if (!String.IsNullOrWhiteSpace(server.identityFile))
                {
                    server.identityFile = Path.GetFullPath(server.identityFile.Trim());
                }
                if (server.allowedCommands == null) server.allowedCommands = new string[0];
            }

            foreach (ProjectServerBinding binding in config.projectServers)
            {
                if (binding == null || String.IsNullOrWhiteSpace(binding.server))
                    throw new InvalidDataException("Each project server binding requires a server.");
                if (!Regex.IsMatch(binding.server, "^[A-Za-z0-9_-]{1,80}$"))
                    throw new InvalidDataException("Project server binding has an invalid server id.");
                if (!String.IsNullOrWhiteSpace(binding.projectPath))
                    binding.projectPath = Path.GetFullPath(binding.projectPath.Trim());
                if (!String.IsNullOrWhiteSpace(binding.projectName))
                    binding.projectName = binding.projectName.Trim();
            }

            return config;
        }

        private static byte[] ReadExact(Stream stream, int length)
        {
            byte[] buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int count = stream.Read(buffer, offset, length - offset);
                if (count == 0)
                    return null;
                offset += count;
            }
            return buffer;
        }

        private static string GetRequestedProjectRoot(Dictionary<string, object> request)
        {
            if (request == null || !request.ContainsKey("params")) return null;
            Dictionary<string, object> parameters = request["params"] as Dictionary<string, object>;
            if (parameters == null || !parameters.ContainsKey("__projectRoot")) return null;
            string value = parameters["__projectRoot"] as string;
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string SafeMessage(Exception ex)
        {
            if (ex == null) return "Unknown bridge error.";
            return (ex.Message ?? ex.GetType().Name).Replace("\r", " ").Replace("\n", " ");
        }
    }

    internal sealed class Bridge
    {
        private const string Version = "1.6.0-autonomous";
        private readonly BridgeConfig _config;
        private readonly string _configPath;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private readonly List<WorkspaceRoot> _roots;
        private readonly WorkspaceRoot _primaryRoot;
        private bool _executionGrantedForRequest;
        private bool _serverExecutionGrantedForRequest;
        private static readonly HashSet<string> BlockedSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".ssh", ".gnupg", "credentials", "secrets", "node_modules", "vendor"
        };
        private static readonly HashSet<string> BlockedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pem", ".key", ".pfx", ".p12", ".jks", ".keystore"
        };
        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "this", "that", "with", "from", "have", "does", "what", "when", "where", "please", "code", "file",
            "برای", "این", "اون", "است", "هست", "باید", "کن", "کنید", "میخوام", "میخوامک", "بررسی", "مشکل", "درست"
        };

        public Bridge(BridgeConfig config, string projectRootOverride = null, string configPath = null)
        {
            _config = config;
            _configPath = configPath;
            _roots = BuildWorkspaceRoots(config, projectRootOverride);
            _primaryRoot = _roots.First(r => r.primary);
        }

        public Dictionary<string, object> Handle(Dictionary<string, object> request)
        {
            if (request == null)
                throw new InvalidDataException("Request must be a JSON object.");

            string action = GetString(request, "action");
            Dictionary<string, object> args = GetDictionary(request, "params");
            _executionGrantedForRequest = GetBool(args, "__executionGranted", false);
            _serverExecutionGrantedForRequest = GetBool(args, "__serverExecutionGranted", false);
            Dictionary<string, object> result;
            using (var gate = new Mutex(false, "Local\\ChatGptAssistantHistory"))
            {
                bool acquired = false;
                try {
                    try { acquired = gate.WaitOne(30000); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("عملیات دیگری در حال اجراست؛ دوباره تلاش کنید.");
                    result = HandleSingleAction(action, args, true);
                } finally { if (acquired) gate.ReleaseMutex(); }
            }

            result["ok"] = true;
            result["action"] = action;
            return result;
        }

        private Dictionary<string, object> HandleSingleAction(string action, Dictionary<string, object> args, bool allowBatch)
        {
            switch ((action ?? String.Empty).Trim().ToLowerInvariant())
            {
                case "ping": return Ping();
                case "choose_project": return ChooseProject();
                case "choose_ssh_key": return ChooseSshKey();
                case "server_profiles": return ServerProfiles();
                case "bind_project_server": return BindProjectServer(args);
                case "save_server_profile": return SaveServerProfile(args);
                case "remove_server_profile": return RemoveServerProfile(args);
                case "test_server": return TestServer(args);
                case "prepare_context": return PrepareContext(args);
                case "search_code": return SearchCode(args);
                case "read_file": return ReadFile(args);
                case "project_tree": return ProjectTree(args);
                case "git_status": return GitStatus(args);
                case "git_diff": return GitDiff(args);
                case "write_files": return WriteFiles(args);
                case "apply_patch": return ApplyPatch(args);
                case "undo": return HistoryRestore(args);
                case "history_list": return HistoryList();
                case "history_restore": return HistoryRestore(args);
                case "history_clear": return HistoryClear();
                case "run_test": return RunTest(args);
                case "exec_local": return ExecLocal(args);
                case "exec_server": return ExecServer(args);
                case "remote_tree": return RemoteTree(args);
                case "remote_search": return RemoteSearch(args);
                case "remote_read_file": return RemoteReadFile(args);
                case "remote_apply_patch": return RemoteApplyPatch(args);
                case "batch":
                    if (!allowBatch) throw new InvalidOperationException("Nested batch actions are not allowed.");
                    return Batch(args);
                default: throw new InvalidOperationException("Unsupported bridge action: " + action);
            }
        }

        private Dictionary<string, object> Ping()
        {
            object[] roots = _roots.Select(r => (object)new Dictionary<string, object>
            {
                { "id", r.id },
                { "name", r.name },
                { "primary", r.primary }
            }).ToArray();

            return new Dictionary<string, object>
            {
                { "version", Version },
                { "projectRoot", _primaryRoot.path },
                { "workspaceFile", _config.workspaceFile ?? String.Empty },
                { "workspaceRoots", roots },
                { "capabilities", new[] { "choose_project", "choose_ssh_key", "server_profiles", "save_server_profile", "remove_server_profile", "test_server", "prepare_context", "search_code", "read_file", "project_tree", "git_status", "git_diff", "write_files", "apply_patch", "undo", "history_list", "history_restore", "history_clear", "run_test", "batch", "exec_local", "exec_server", "remote_tree", "remote_search", "remote_read_file", "remote_apply_patch" } },
                { "availableTests", _config.tests.Select(t => t.id).Where(s => !String.IsNullOrWhiteSpace(s)).ToArray() },
                { "rgAvailable", FindExecutable("rg.exe") != null },
                { "gitAvailable", FindExecutable("git.exe") != null },
                { "executionEnabled", _config.executionEnabled },
                { "executionGranted", _executionGrantedForRequest },
                { "serverExecutionGranted", _serverExecutionGrantedForRequest },
                { "powershellAvailable", FindPowerShell() != null },
                { "sshAvailable", FindSsh() != null },
                { "servers", _config.servers.Select(s => s.id).ToArray() },
                { "defaultServer", ResolveDefaultServerId() ?? String.Empty },
                { "allowedLocalCommands", _config.allowedLocalCommands },
                { "allowedLocalScripts", _config.allowedLocalScripts }
            };
        }

        private Dictionary<string, object> ChooseProject()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "پوشه پروژه‌ای را انتخاب کنید که Project Bridge باید با آن کار کند.";
                dialog.ShowNewFolderButton = false;
                dialog.SelectedPath = _primaryRoot.path;

                if (dialog.ShowDialog() != DialogResult.OK || String.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    return new Dictionary<string, object> { { "cancelled", true } };
                }

                string selected = Path.GetFullPath(dialog.SelectedPath);
                if (!Directory.Exists(selected))
                    throw new DirectoryNotFoundException("Selected project folder does not exist: " + selected);

                string name = Path.GetFileName(selected.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return new Dictionary<string, object>
                {
                    { "cancelled", false },
                    { "projectRoot", selected },
                    { "projectName", String.IsNullOrWhiteSpace(name) ? selected : name },
                    { "projectId", MakeRootId(name) }
                };
            }
        }

        private Dictionary<string, object> ChooseSshKey()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "کلید خصوصی SSH را انتخاب کنید";
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                dialog.Filter = "SSH private key|id_*;*.pem;*.key|All files|*.*";

                if (dialog.ShowDialog() != DialogResult.OK || String.IsNullOrWhiteSpace(dialog.FileName))
                    return new Dictionary<string, object> { { "cancelled", true } };

                string selected = Path.GetFullPath(dialog.FileName);
                if (!File.Exists(selected))
                    throw new FileNotFoundException("Selected SSH key does not exist.");

                return new Dictionary<string, object>
                {
                    { "cancelled", false },
                    { "path", selected },
                    { "name", Path.GetFileName(selected) }
                };
            }
        }

        private Dictionary<string, object> ServerProfiles()
        {
            object[] profiles = _config.servers.Select(server => (object)new Dictionary<string, object>
            {
                { "id", server.id },
                { "host", server.host },
                { "user", server.user },
                { "port", server.port <= 0 ? 22 : server.port },
                { "workingDirectory", server.workingDirectory ?? String.Empty },
                { "identityFileName", String.IsNullOrWhiteSpace(server.identityFile) ? String.Empty : Path.GetFileName(server.identityFile) },
                { "hasIdentityFile", !String.IsNullOrWhiteSpace(server.identityFile) },
                { "allowedCommands", server.allowedCommands ?? new string[0] }
            }).ToArray();

            return new Dictionary<string, object> { { "servers", profiles }, { "defaultServer", ResolveDefaultServerId() ?? String.Empty } };
        }

        private Dictionary<string, object> BindProjectServer(Dictionary<string, object> args)
        {
            string id = RequireString(args, "server", 1, 80);
            if (!_config.servers.Any(server => String.Equals(server.id, id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Unknown server profile: " + id);
            var bindings = _config.projectServers.Where(binding =>
                !String.Equals(binding.projectPath, _primaryRoot.path, StringComparison.OrdinalIgnoreCase)).ToList();
            bindings.Insert(0, new ProjectServerBinding { projectPath = _primaryRoot.path, server = id });
            _config.projectServers = bindings.ToArray();
            File.WriteAllText(_configPath, _json.Serialize(_config), new UTF8Encoding(false));
            return new Dictionary<string, object> { { "server", id }, { "projectRoot", _primaryRoot.path } };
        }

        private Dictionary<string, object> SaveServerProfile(Dictionary<string, object> args)
        {
            if (String.IsNullOrWhiteSpace(_configPath))
                throw new InvalidOperationException("Native config path is unavailable.");

            string id = RequireString(args, "id", 1, 80).Trim();
            string host = RequireString(args, "host", 1, 255).Trim();
            string user = RequireString(args, "user", 1, 80).Trim();
            string workingDirectory = RequireString(args, "workingDirectory", 1, 1000).Trim();
            string identityFile = GetString(args, "identityFile") ?? String.Empty;
            int port = GetInt(args, "port", 22, 1, 65535);
            ServerProfile existingProfile = _config.servers.FirstOrDefault(server =>
                String.Equals(server.id, id, StringComparison.OrdinalIgnoreCase));
            if (String.IsNullOrWhiteSpace(identityFile) && existingProfile != null)
                identityFile = existingProfile.identityFile ?? String.Empty;

            if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,80}$"))
                throw new InvalidDataException("Server profile id is invalid.");
            if (host.Any(Char.IsWhiteSpace) || host.IndexOfAny(new[] { ';', '`', '$', '|', '&', '<', '>' }) >= 0)
                throw new InvalidDataException("Server host is invalid.");
            if (!Regex.IsMatch(user, "^[A-Za-z0-9._-]{1,80}$"))
                throw new InvalidDataException("Server user is invalid.");
            if (!workingDirectory.StartsWith("/", StringComparison.Ordinal) ||
                workingDirectory.IndexOfAny(new[] { '\r', '\n', '\0', ';', '`', '$', '|', '&', '<', '>' }) >= 0)
                throw new InvalidDataException("Server workingDirectory must be a safe absolute Linux path.");

            if (String.IsNullOrWhiteSpace(identityFile))
                throw new InvalidDataException("SSH identity file is required.");
            identityFile = Path.GetFullPath(identityFile);
            if (!File.Exists(identityFile))
                throw new FileNotFoundException("SSH identity file was not found.");

            string[] commands = GetObjectArray(args, "allowedCommands")
                .Select(item => Convert.ToString(item) ?? String.Empty)
                .Select(item => item.Trim())
                .Where(item => !String.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToArray();
            if (commands.Length == 0)
                throw new InvalidDataException("At least one allowed remote command prefix is required.");

            foreach (string command in commands)
            {
                if (command.Length > 500 ||
                    command.IndexOfAny(new[] { '\r', '\n', '\0', ';', '`', '>', '<' }) >= 0 ||
                    command.Contains("&&") || command.Contains("||") || command.Contains("$("))
                    throw new InvalidDataException("An allowed command prefix contains unsafe shell syntax.");
            }

            ServerProfile profile = new ServerProfile
            {
                id = id,
                host = host,
                user = user,
                port = port,
                identityFile = identityFile,
                workingDirectory = workingDirectory,
                allowedCommands = commands
            };

            List<ServerProfile> servers = _config.servers.ToList();
            int existing = servers.FindIndex(server =>
                String.Equals(server.id, id, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) servers[existing] = profile;
            else servers.Add(profile);

            _config.servers = servers.ToArray();
            File.WriteAllText(_configPath, _json.Serialize(_config), new UTF8Encoding(false));

            return new Dictionary<string, object>
            {
                { "saved", true },
                { "server", id }
            };
        }

        private Dictionary<string, object> RemoveServerProfile(Dictionary<string, object> args)
        {
            if (String.IsNullOrWhiteSpace(_configPath))
                throw new InvalidOperationException("Native config path is unavailable.");

            string id = RequireString(args, "server", 1, 80);
            int before = _config.servers.Length;
            _config.servers = _config.servers
                .Where(server => !String.Equals(server.id, id, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (_config.servers.Length == before)
                throw new InvalidOperationException("Unknown server profile: " + id);

            File.WriteAllText(_configPath, _json.Serialize(_config), new UTF8Encoding(false));
            return new Dictionary<string, object> { { "removed", true }, { "server", id } };
        }

        private Dictionary<string, object> TestServer(Dictionary<string, object> args)
        {
            EnsureServerExecutionAllowed();
            ServerProfile server = ResolveServer(args);
            ProcessResult result = RunServerCommand(server, "pwd", GetInt(args, "timeoutSeconds", 15, 1, 60));
            return new Dictionary<string, object>
            {
                { "server", server.id },
                { "connected", result.ExitCode == 0 && !result.TimedOut },
                { "exitCode", result.ExitCode },
                { "timedOut", result.TimedOut },
                { "output", Truncate(result.Output, 12000) },
                { "errorOutput", Truncate(result.Error, 12000) }
            };
        }

        private Dictionary<string, object> PrepareContext(Dictionary<string, object> args)
        {
            string prompt = RequireString(args, "prompt", 1, 20000);
            List<string> terms = ExtractTerms(prompt).Take(10).ToList();
            string pattern = terms.Count == 0 ? Regex.Escape(prompt.Trim()) : String.Join("|", terms.Select(Regex.Escape));
            ProcessResult search = RunRgAcrossRoots(pattern, Math.Min(_config.maxSearchResults, 60), "all");
            StringBuilder context = new StringBuilder();
            context.AppendLine("Workspace roots: " + String.Join(", ", _roots.Select(r => r.id)));
            context.AppendLine("Primary root: " + _primaryRoot.id);
            context.AppendLine("Relevant search results:");
            context.AppendLine(Truncate(search.Output, Math.Min(_config.maxContextChars, 50000)));

            if (String.IsNullOrWhiteSpace(search.Output))
            {
                context.AppendLine("No direct text match was found. Workspace file overview:");
                context.AppendLine(Truncate(GetProjectFilesAcrossRoots(140, "all"), 18000));
            }

            return new Dictionary<string, object>
            {
                { "context", Truncate(context.ToString(), _config.maxContextChars) },
                { "terms", terms.ToArray() },
                { "roots", _roots.Select(r => r.id).ToArray() }
            };
        }

        private Dictionary<string, object> SearchCode(Dictionary<string, object> args)
        {
            string query = RequireString(args, "query", 1, 1000);
            string rootId = GetString(args, "root") ?? "all";
            bool fixedString = GetBool(args, "fixed", false);
            string pattern = fixedString ? Regex.Escape(query) : query;
            ValidateRegex(pattern);
            ProcessResult result = RunRgAcrossRoots(pattern, _config.maxSearchResults, rootId);
            return new Dictionary<string, object>
            {
                { "query", query },
                { "root", rootId },
                { "results", Truncate(result.Output, 160000) },
                { "exitCode", result.ExitCode },
                { "timedOut", result.TimedOut }
            };
        }

        private Dictionary<string, object> ReadFile(Dictionary<string, object> args)
        {
            string relative = RequireString(args, "path", 1, 1000);
            string requestedRoot = GetString(args, "root");
            WorkspaceRoot root = ResolveRootForPath(ref relative, requestedRoot);
            int start = GetInt(args, "startLine", 1, 1, 10000000);
            int end = GetInt(args, "endLine", Math.Min(start + 399, 10000000), start, Math.Min(start + 1999, 10000000));
            string full = ResolveProjectPath(root, relative, true);
            FileInfo info = new FileInfo(full);
            if (info.Length > _config.maxReadBytes * 8L)
                throw new InvalidOperationException("File is too large for bridge reading.");

            string[] lines = ReadTextLines(full, _config.maxReadBytes * 8);
            int actualEnd = Math.Min(end, lines.Length);
            StringBuilder content = new StringBuilder();
            for (int i = start; i <= actualEnd; i++)
                content.AppendLine(i.ToString().PadLeft(6) + " | " + lines[i - 1]);

            return new Dictionary<string, object>
            {
                { "root", root.id },
                { "path", NormalizeRelative(relative) },
                { "startLine", start },
                { "endLine", actualEnd },
                { "totalLines", lines.Length },
                { "content", Truncate(content.ToString(), _config.maxReadBytes) }
            };
        }

        private Dictionary<string, object> ProjectTree(Dictionary<string, object> args)
        {
            int limit = GetInt(args, "limit", 400, 1, 1200);
            string rootId = GetString(args, "root") ?? "all";
            string files = GetProjectFilesAcrossRoots(limit, rootId);
            return new Dictionary<string, object>
            {
                { "root", rootId },
                { "workspaceRoots", _roots.Select(r => r.id).ToArray() },
                { "files", files },
                { "limit", limit }
            };
        }

        private Dictionary<string, object> GitStatus(Dictionary<string, object> args)
        {
            string rootId = GetString(args, "root") ?? _primaryRoot.id;
            WorkspaceRoot root = ResolveRoot(rootId, false);
            ProcessResult result = RunGit(root, "status --short --branch", _config.processTimeoutSeconds);
            EnsureSuccess(result, "git status");
            return new Dictionary<string, object>
            {
                { "root", root.id },
                { "status", Truncate(result.Output, 100000) }
            };
        }

        private Dictionary<string, object> GitDiff(Dictionary<string, object> args)
        {
            string relative = GetString(args, "path");
            string requestedRoot = GetString(args, "root");
            WorkspaceRoot root = String.IsNullOrWhiteSpace(relative)
                ? ResolveRoot(requestedRoot ?? _primaryRoot.id, false)
                : ResolveRootForPath(ref relative, requestedRoot);

            string arguments = "diff --no-ext-diff --unified=3";
            if (!String.IsNullOrWhiteSpace(relative))
            {
                ResolveProjectPath(root, relative, false);
                arguments += " -- " + QuoteArg(NormalizeRelative(relative));
            }
            ProcessResult result = RunGit(root, arguments, _config.processTimeoutSeconds);
            EnsureSuccess(result, "git diff");
            return new Dictionary<string, object>
            {
                { "root", root.id },
                { "diff", Truncate(result.Output, 500000) }
            };
        }

        private Dictionary<string, object> WriteFiles(Dictionary<string, object> args)
        {
            string rootId = GetString(args, "root") ?? _primaryRoot.id;
            WorkspaceRoot root = ResolveRoot(rootId, false);
            object[] rawFiles = GetObjectArray(args, "files");
            object[] rawDirectories = GetObjectArray(args, "directories");
            bool overwrite = GetBool(args, "overwrite", false);

            if (rawFiles.Length == 0 && rawDirectories.Length == 0)
                throw new InvalidDataException("write_files requires at least one file or directory.");
            if (rawFiles.Length > 100)
                throw new InvalidDataException("write_files may contain at most 100 files.");
            if (rawDirectories.Length > 100)
                throw new InvalidDataException("write_files may contain at most 100 explicit directories.");

            List<Dictionary<string, object>> files = new List<Dictionary<string, object>>();
            HashSet<string> filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalCharacters = 0;

            foreach (object raw in rawDirectories)
            {
                string relative = NormalizeRelative(Convert.ToString(raw));
                if (String.IsNullOrWhiteSpace(relative))
                    throw new InvalidDataException("Directory paths must be non-empty strings.");
                if (relative.Length > 1000)
                    throw new InvalidDataException("A directory path exceeds its allowed length.");
                string full = ResolveProjectPath(root, relative, false);
                if (File.Exists(full))
                    throw new IOException("A file already exists at directory path: " + relative);
                directoryPaths.Add(relative);
            }

            foreach (object raw in rawFiles)
            {
                Dictionary<string, object> item = raw as Dictionary<string, object>;
                if (item == null)
                    throw new InvalidDataException("Each write_files.files item must be an object.");

                string relative = NormalizeRelative(RequireString(item, "path", 1, 1000));
                string content = GetString(item, "content");
                if (content == null)
                    throw new InvalidDataException("Each file requires a content string (an empty string is allowed).");
                if (content.Length > 700000)
                    throw new InvalidDataException("A file exceeds the 700,000 character write limit: " + relative);
                totalCharacters += content.Length;
                if (totalCharacters > 2000000)
                    throw new InvalidDataException("write_files exceeds the 2,000,000 total character limit.");
                if (!filePaths.Add(relative))
                    throw new InvalidDataException("Duplicate file path in write_files: " + relative);

                string full = ResolveProjectPath(root, relative, false);
                if (Directory.Exists(full))
                    throw new IOException("A directory already exists at file path: " + relative);
                if (File.Exists(full) && !overwrite)
                    throw new IOException("File already exists; set overwrite:true only when replacement is intended: " + relative);

                string parent = NormalizeRelative(Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar)));
                while (!String.IsNullOrWhiteSpace(parent))
                {
                    directoryPaths.Add(parent);
                    parent = NormalizeRelative(Path.GetDirectoryName(parent.Replace('/', Path.DirectorySeparatorChar)));
                }

                files.Add(new Dictionary<string, object>
                {
                    { "path", relative },
                    { "full", full },
                    { "content", content }
                });
            }

            if (directoryPaths.Any(path => filePaths.Contains(path)))
                throw new InvalidDataException("A path cannot be both a file and a directory in the same write_files request.");

            foreach (string directory in directoryPaths)
            {
                string full = ResolveProjectPath(root, directory, false);
                if (File.Exists(full))
                    throw new IOException("A file blocks directory creation: " + directory);
            }

            HashSet<string> createdDirectoryPaths = new HashSet<string>(
                directoryPaths.Where(path => !Directory.Exists(ResolveProjectPath(root, path, false))),
                StringComparer.OrdinalIgnoreCase
            );
            string checkpointId = CreateCheckpoint(root, filePaths, directoryPaths);
            try
            {
                foreach (string directory in directoryPaths.OrderBy(path => path.Count(c => c == '/')))
                    Directory.CreateDirectory(ResolveProjectPath(root, directory, false));

                foreach (Dictionary<string, object> file in files)
                {
                    string full = Convert.ToString(file["full"]);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    File.WriteAllText(full, Convert.ToString(file["content"]), new UTF8Encoding(false));
                }
            }
            catch
            {
                RestoreCheckpoint(checkpointId);
                Directory.Delete(Path.Combine(_config.dataDirectory, "checkpoints", checkpointId), true);
                throw;
            }

            return new Dictionary<string, object>
            {
                { "root", root.id },
                { "checkpointId", checkpointId },
                { "writtenFiles", filePaths.OrderBy(path => path).ToArray() },
                { "createdDirectories", createdDirectoryPaths.OrderBy(path => path).ToArray() },
                { "fileCount", files.Count },
                { "directoryCount", directoryPaths.Count },
                { "overwroteExisting", overwrite },
                { "verified", files.All(file => VerifyWrittenFile(file)) && directoryPaths.All(directory => Directory.Exists(ResolveProjectPath(root, directory, false))) },
                { "artifacts", files
                    .OrderBy(file => Convert.ToString(file["path"]))
                    .Select(file => (object)DescribeWrittenFile(file))
                    .Concat(directoryPaths.OrderBy(path => path).Select(directory => (object)new Dictionary<string, object>
                    {
                        { "path", directory },
                        { "type", "directory" },
                        { "exists", Directory.Exists(ResolveProjectPath(root, directory, false)) }
                    }))
                    .ToArray() }
            };
        }

        private static bool VerifyWrittenFile(Dictionary<string, object> file)
        {
            string full = Convert.ToString(file["full"]);
            string expected = Convert.ToString(file["content"]);
            return File.Exists(full) && String.Equals(File.ReadAllText(full, Encoding.UTF8), expected, StringComparison.Ordinal);
        }

        private static Dictionary<string, object> DescribeWrittenFile(Dictionary<string, object> file)
        {
            string full = Convert.ToString(file["full"]);
            bool exists = File.Exists(full);
            return new Dictionary<string, object>
            {
                { "path", Convert.ToString(file["path"]) },
                { "type", "file" },
                { "exists", exists },
                { "bytes", exists ? new FileInfo(full).Length : 0L },
                { "sha256", exists ? ComputeSha256(full) : String.Empty }
            };
        }

        private static string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private Dictionary<string, object> ApplyPatch(Dictionary<string, object> args)
        {
            string rootId = GetString(args, "root") ?? _primaryRoot.id;
            WorkspaceRoot root = ResolveRoot(rootId, false);
            string patch = RequireString(args, "patch", 1, 700000).Replace("\r\n", "\n");
            if (!patch.EndsWith("\n", StringComparison.Ordinal))
                patch += "\n";
            List<string> paths = GetPatchPaths(patch);
            if (paths.Count == 0)
                throw new InvalidDataException("Patch has no recognized file paths.");
            foreach (string path in paths)
                ResolveProjectPath(root, path, false);

            string patchDirectory = Path.Combine(_config.dataDirectory, "patches");
            Directory.CreateDirectory(patchDirectory);
            string patchPath = Path.Combine(patchDirectory, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + ".patch");
            File.WriteAllText(patchPath, patch, new UTF8Encoding(false));

            ProcessResult check = RunGit(root, "apply --check --recount --whitespace=nowarn " + QuoteArg(patchPath), _config.processTimeoutSeconds);
            EnsureSuccess(check, "git apply --check");

            string checkpointId = CreateCheckpoint(root, paths);
            ProcessResult apply = RunGit(root, "apply --recount --whitespace=nowarn " + QuoteArg(patchPath), _config.processTimeoutSeconds);
            if (apply.ExitCode != 0 || apply.TimedOut)
            {
                RestoreCheckpoint(checkpointId);
                Directory.Delete(Path.Combine(_config.dataDirectory, "checkpoints", checkpointId), true);
                EnsureSuccess(apply, "git apply");
            }

            string pathArguments = String.Join(" ", paths.Select(QuoteArg));
            ProcessResult diff = RunGit(root, "diff --no-ext-diff --unified=3 -- " + pathArguments, _config.processTimeoutSeconds);
            return new Dictionary<string, object>
            {
                { "root", root.id },
                { "checkpointId", checkpointId },
                { "changedFiles", paths.ToArray() },
                { "diff", Truncate(diff.Output, 400000) }
            };
        }

        private Dictionary<string, object> Undo(Dictionary<string, object> args)
        {
            string checkpointId = RequireString(args, "checkpointId", 3, 120);
            if (!Regex.IsMatch(checkpointId, "^[A-Za-z0-9_-]+$"))
                throw new InvalidDataException("Checkpoint ID is invalid.");
            string[] restored = RestoreCheckpoint(checkpointId);
            return new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "restoredFiles", restored }
            };
        }

        private Dictionary<string, object> RunTest(Dictionary<string, object> args)
        {
            string id = RequireString(args, "id", 1, 100);
            TestProfile profile = _config.tests.FirstOrDefault(t => String.Equals(t.id, id, StringComparison.OrdinalIgnoreCase));
            if (profile == null)
                throw new InvalidOperationException("Test profile is not allowed: " + id);

            string requestedRoot = GetString(args, "root");
            WorkspaceRoot root = ResolveRoot(requestedRoot ?? profile.root ?? _primaryRoot.id, false);
            string working = root.path;
            if (!String.IsNullOrWhiteSpace(profile.workingDirectory) && profile.workingDirectory != ".")
                working = ResolveProjectPath(root, profile.workingDirectory, false);
            if (!Directory.Exists(working))
                throw new DirectoryNotFoundException("Test working directory does not exist.");

            int timeout = profile.timeoutSeconds > 0 ? Math.Min(profile.timeoutSeconds, 1800) : 120;
            ProcessResult result = RunProcess(profile.fileName, profile.arguments ?? String.Empty, working, timeout, 500000);
            return new Dictionary<string, object>
            {
                { "id", id },
                { "root", root.id },
                { "exitCode", result.ExitCode },
                { "timedOut", result.TimedOut },
                { "passed", result.ExitCode == 0 && !result.TimedOut },
                { "output", Truncate(result.Output, 350000) },
                { "errorOutput", Truncate(result.Error, 120000) }
            };
        }

        private Dictionary<string, object> Batch(Dictionary<string, object> args)
        {
            object[] actions = GetObjectArray(args, "actions");
            if (actions.Length == 0)
                throw new InvalidDataException("batch.actions is required.");
            if (actions.Length > 8)
                throw new InvalidDataException("A batch may contain at most 8 actions.");

            HashSet<string> allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "prepare_context",
                "search_code",
                "read_file",
                "project_tree",
                "git_status",
                "git_diff",
                "run_test",
                "exec_local",
                "exec_server",
                "remote_tree",
                "remote_search",
                "remote_read_file"
            };

            List<object> results = new List<object>();
            int failed = 0;

            foreach (object raw in actions)
            {
                Dictionary<string, object> item = raw as Dictionary<string, object>;
                string action = item == null ? null : GetString(item, "action");
                Dictionary<string, object> childArgs = item == null
                    ? new Dictionary<string, object>()
                    : GetDictionary(item, "args");

                if (String.IsNullOrWhiteSpace(action) || !allowed.Contains(action))
                {
                    failed++;
                    results.Add(new Dictionary<string, object>
                    {
                        { "action", action ?? "unknown" },
                        { "ok", false },
                        { "error", "This action is not allowed inside a batch." }
                    });
                    continue;
                }

                try
                {
                    Dictionary<string, object> child = HandleSingleAction(action, childArgs, false);
                    results.Add(new Dictionary<string, object>
                    {
                        { "action", action },
                        { "ok", true },
                        { "result", CompactBatchValue(child, 0) }
                    });
                }
                catch (Exception ex)
                {
                    failed++;
                    results.Add(new Dictionary<string, object>
                    {
                        { "action", action },
                        { "ok", false },
                        { "error", Truncate((ex.Message ?? ex.GetType().Name).Replace("\r", " ").Replace("\n", " "), 3000) }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "count", actions.Length },
                { "failed", failed },
                { "results", results.ToArray() }
            };
        }

        private object CompactBatchValue(object value, int depth)
        {
            if (value == null) return null;
            if (depth > 5) return "[batch value depth truncated]";

            string text = value as string;
            if (text != null) return Truncate(text, 8000);

            Dictionary<string, object> dictionary = value as Dictionary<string, object>;
            if (dictionary != null)
            {
                Dictionary<string, object> compact = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> pair in dictionary)
                    compact[pair.Key] = CompactBatchValue(pair.Value, depth + 1);
                return compact;
            }

            object[] array = value as object[];
            if (array != null)
                return array.Take(30).Select(item => CompactBatchValue(item, depth + 1)).ToArray();

            return value;
        }

        private Dictionary<string, object> ExecLocal(Dictionary<string, object> args)
        {
            EnsureExecutionAllowed();
            WorkspaceRoot root = ResolveRoot(GetString(args, "root") ?? _primaryRoot.id, false);
            string working = root.path;
            string relativeWorking = GetString(args, "workingDirectory");
            if (!String.IsNullOrWhiteSpace(relativeWorking) && relativeWorking != ".")
            {
                working = ResolveProjectPath(root, relativeWorking, false);
                if (!Directory.Exists(working)) throw new DirectoryNotFoundException("Execution working directory does not exist.");
            }
            string arguments = GetString(args, "arguments") ?? String.Empty;
            if (arguments.Length > 12000 || Regex.IsMatch(arguments, @"[A-Za-z]:[\\/]") ||
                arguments.Contains("../") || arguments.Contains("..\\") ||
                Regex.IsMatch(arguments, @"(^|[\\/])\.env|(^|[\\/])\.ssh|credentials?|secrets?", RegexOptions.IgnoreCase))
                throw new UnauthorizedAccessException("Execution arguments contain a blocked path or sensitive reference.");

            string executable;
            string processArguments;
            string mode;
            string script = GetString(args, "script");
            if (!String.IsNullOrWhiteSpace(script))
            {
                string normalized = NormalizeRelative(script);
                if (!_config.allowedLocalScripts.Any(x => String.Equals(NormalizeRelative(x), normalized, StringComparison.OrdinalIgnoreCase)))
                    throw new UnauthorizedAccessException("Local script is not allowlisted.");
                string fullScript = ResolveProjectPath(root, normalized, true);
                if (!String.Equals(Path.GetExtension(fullScript), ".ps1", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Only allowlisted .ps1 scripts may be executed.");
                executable = FindPowerShell();
                if (executable == null) throw new FileNotFoundException("PowerShell was not found.");
                processArguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + QuoteArg(fullScript) +
                    (String.IsNullOrWhiteSpace(arguments) ? String.Empty : " " + arguments);
                mode = "powershell_script";
            }
            else
            {
                string command = RequireString(args, "command", 1, 120).Trim();
                if (new[] { "ssh", "ssh.exe", "scp", "scp.exe", "sftp", "sftp.exe" }.Contains(command, StringComparer.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("SSH tools must use exec_server with a configured server profile.");
                if (!Regex.IsMatch(command, "^[A-Za-z0-9._-]+$") ||
                    !_config.allowedLocalCommands.Any(x => String.Equals((x ?? String.Empty).Trim(), command, StringComparison.OrdinalIgnoreCase)))
                    throw new UnauthorizedAccessException("Local command is not allowlisted.");
                executable = FindExecutable(command);
                if (executable == null) throw new FileNotFoundException("Allowlisted executable was not found in PATH: " + command);
                processArguments = arguments;
                mode = "executable";
            }
            ProcessResult result = RunProcess(executable, processArguments, working, GetInt(args, "timeoutSeconds", _config.processTimeoutSeconds, 1, 1800), 160000);
            return new Dictionary<string, object>
            {
                { "root", root.id }, { "mode", mode }, { "exitCode", result.ExitCode }, { "timedOut", result.TimedOut },
                { "passed", result.ExitCode == 0 && !result.TimedOut },
                { "output", Truncate(result.Output, 120000) }, { "errorOutput", Truncate(result.Error, 40000) }
            };
        }

        private Dictionary<string, object> ExecServer(Dictionary<string, object> args)
        {
            EnsureServerExecutionAllowed();
            ServerProfile server = ResolveServer(args, false);
            string command = RequireString(args, "command", 1, 8000).Trim();
            if (command.IndexOfAny(new[] { '\r', '\n', '\0', ';', '`', '>', '<' }) >= 0 ||
                command.Contains("&&") || command.Contains("||") || command.Contains("$(") ||
                Regex.IsMatch(command, @"(^|[\\/])\.env|(^|[\\/])\.ssh|credentials?|secrets?", RegexOptions.IgnoreCase))
                throw new UnauthorizedAccessException("Unsafe remote command syntax or sensitive reference.");
            bool allowed = (server.allowedCommands ?? new string[0]).Any(prefix =>
                !String.IsNullOrWhiteSpace(prefix) &&
                (String.Equals(command, prefix.Trim(), StringComparison.OrdinalIgnoreCase) ||
                 command.StartsWith(prefix.Trim() + " ", StringComparison.OrdinalIgnoreCase)));
            if (!allowed) throw new UnauthorizedAccessException("Remote command is not allowlisted for server profile: " + server.id);
            string ssh = FindSsh();
            if (ssh == null) throw new FileNotFoundException("OpenSSH ssh.exe was not found in PATH.");
            string remote = String.IsNullOrWhiteSpace(server.workingDirectory)
                ? command : "cd " + QuotePosix(server.workingDirectory.Trim()) + " && " + command;
            string sshArgs = "-o BatchMode=yes -o StrictHostKeyChecking=yes -p " + (server.port <= 0 ? 22 : server.port) + " " +
                (!String.IsNullOrWhiteSpace(server.identityFile) ? "-i " + QuoteArg(server.identityFile) + " " : String.Empty) +
                QuoteArg(server.user + "@" + server.host) + " " + QuoteArg(remote);
            ProcessResult result = RunProcess(ssh, sshArgs, _primaryRoot.path, GetInt(args, "timeoutSeconds", _config.processTimeoutSeconds, 1, 1800), 160000);
            return new Dictionary<string, object> { { "server", server.id }, { "exitCode", result.ExitCode }, { "timedOut", result.TimedOut }, { "passed", result.ExitCode == 0 && !result.TimedOut }, { "output", Truncate(result.Output, 120000) }, { "errorOutput", Truncate(result.Error, 40000) } };
        }

        private Dictionary<string, object> RemoteTree(Dictionary<string, object> args)
        {
            ServerProfile server = ResolveServer(args);
            string path = NormalizeRemoteRelative(GetString(args, "path"));
            int limit = GetInt(args, "limit", 200, 1, 1000);
            string target = String.IsNullOrEmpty(path) ? "." : "./" + path;
            string command =
                "find " + QuotePosix(target) +
                " -maxdepth 6 -type f" +
                " ! -path '*/.git/*' ! -path '*/node_modules/*' ! -path '*/vendor/*'" +
                " ! -name '.env' ! -name '.env.*' ! -name '*.pem' ! -name '*.key'" +
                " | head -n " + limit;
            ProcessResult result = RunServerCommand(server, command, GetInt(args, "timeoutSeconds", _config.processTimeoutSeconds, 1, 180));
            return new Dictionary<string, object>
            {
                { "server", server.id },
                { "path", path },
                { "files", Truncate(result.Output, 120000) },
                { "exitCode", result.ExitCode },
                { "timedOut", result.TimedOut }
            };
        }

        private Dictionary<string, object> RemoteSearch(Dictionary<string, object> args)
        {
            ServerProfile server = ResolveServer(args);
            string query = RequireString(args, "query", 1, 500);
            if (query.IndexOf('\0') >= 0 || query.IndexOf('\r') >= 0 || query.IndexOf('\n') >= 0)
                throw new InvalidDataException("Remote search query is invalid.");
            int limit = GetInt(args, "limit", _config.maxSearchResults, 1, 300);
            string command =
                "grep -RInF --binary-files=without-match" +
                " --exclude='.env' --exclude='.env.*' --exclude='*.pem' --exclude='*.key'" +
                " --exclude-dir=.git --exclude-dir=node_modules --exclude-dir=vendor" +
                " -- " + QuotePosix(query) + " . | head -n " + limit;
            ProcessResult result = RunServerCommand(server, command, GetInt(args, "timeoutSeconds", _config.processTimeoutSeconds, 1, 180));
            return new Dictionary<string, object>
            {
                { "server", server.id },
                { "query", query },
                { "results", Truncate(result.Output, 140000) },
                { "exitCode", result.ExitCode },
                { "timedOut", result.TimedOut }
            };
        }

        private Dictionary<string, object> RemoteReadFile(Dictionary<string, object> args)
        {
            ServerProfile server = ResolveServer(args);
            string path = NormalizeRemoteRelative(RequireString(args, "path", 1, 1000));
            EnsureRemotePathAllowed(path);
            int start = GetInt(args, "startLine", 1, 1, 10000000);
            int end = GetInt(args, "endLine", start + 299, start, Math.Min(10000000, start + 1999));
            string command =
                "if [ ! -f " + QuotePosix("./" + path) + " ]; then echo 'REMOTE_FILE_NOT_FOUND' >&2; exit 44; fi; " +
                "sed -n " + QuotePosix(start + "," + end + "p") + " " + QuotePosix("./" + path);
            ProcessResult result = RunServerCommand(server, command, GetInt(args, "timeoutSeconds", _config.processTimeoutSeconds, 1, 180));
            if (result.ExitCode == 44)
                throw new FileNotFoundException("Remote file was not found: " + path);
            return new Dictionary<string, object>
            {
                { "server", server.id },
                { "path", path },
                { "startLine", start },
                { "endLine", end },
                { "content", Truncate(result.Output, _config.maxReadBytes) },
                { "exitCode", result.ExitCode },
                { "timedOut", result.TimedOut }
            };
        }

        private Dictionary<string, object> RemoteApplyPatch(Dictionary<string, object> args)
        {
            EnsureServerExecutionAllowed();
            ServerProfile server = ResolveServer(args);
            string patch = RequireString(args, "patch", 10, 700000);
            if (!patch.Contains("diff --git a/") || !patch.Contains("\n--- ") || !patch.Contains("\n+++ ") || !patch.Contains("\n@@"))
                throw new InvalidDataException("A valid unified Git patch is required.");
            if (Regex.IsMatch(patch, @"(^|\n)(---|\+\+\+) [ab]/(?:\.env|\.ssh|credentials?|secrets?)(?:/|$)", RegexOptions.IgnoreCase))
                throw new UnauthorizedAccessException("Patch targets a blocked or sensitive remote path.");

            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(patch));
            string patchFile = ".project-bridge-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".patch";
            string command =
                "printf %s " + QuotePosix(encoded) + " | base64 -d > " + QuotePosix(patchFile) +
                " && git apply --check " + QuotePosix(patchFile) +
                " && git apply --whitespace=nowarn " + QuotePosix(patchFile) +
                " ; code=$?; rm -f " + QuotePosix(patchFile) + "; exit $code";
            ProcessResult result = RunServerCommand(server, command, GetInt(args, "timeoutSeconds", 120, 1, 600));
            if (result.ExitCode != 0 || result.TimedOut)
                throw new InvalidOperationException("Remote patch failed: " + Truncate(result.Error, 3000));
            return new Dictionary<string, object>
            {
                { "server", server.id },
                { "applied", true },
                { "exitCode", result.ExitCode },
                { "timedOut", result.TimedOut },
                { "output", Truncate(result.Output, 30000) }
            };
        }

        private ServerProfile ResolveServer(Dictionary<string, object> args)
        {
            return ResolveServer(args, true);
        }

        private ServerProfile ResolveServer(Dictionary<string, object> args, bool requiresWorkingDirectory)
        {
            EnsureServerExecutionAllowed();
            string serverId = GetString(args, "server");
            if (String.IsNullOrWhiteSpace(serverId))
                serverId = ResolveDefaultServerId();
            if (String.IsNullOrWhiteSpace(serverId))
                throw new InvalidOperationException("No server profile was specified and no default server is linked to this project.");

            serverId = serverId.Trim();
            ServerProfile server = _config.servers.FirstOrDefault(s =>
                String.Equals(s.id, serverId, StringComparison.OrdinalIgnoreCase));
            if (server == null) throw new InvalidOperationException("Unknown server profile: " + serverId);
            if (requiresWorkingDirectory && String.IsNullOrWhiteSpace(server.workingDirectory))
                throw new InvalidOperationException("Server profile requires workingDirectory for remote project actions: " + server.id);
            return server;
        }

        private string ResolveDefaultServerId()
        {
            ProjectServerBinding binding = (_config.projectServers ?? new ProjectServerBinding[0]).FirstOrDefault(item =>
                item != null &&
                !String.IsNullOrWhiteSpace(item.server) &&
                (
                    (!String.IsNullOrWhiteSpace(item.projectPath) &&
                     String.Equals(Path.GetFullPath(item.projectPath), _primaryRoot.path, StringComparison.OrdinalIgnoreCase)) ||
                    (!String.IsNullOrWhiteSpace(item.projectName) &&
                     String.Equals(item.projectName, _primaryRoot.name, StringComparison.OrdinalIgnoreCase))
                ));

            if (binding != null && _config.servers.Any(server => String.Equals(server.id, binding.server, StringComparison.OrdinalIgnoreCase)))
                return binding.server.Trim();

            bool looksLikeSitesaz = String.Equals(_primaryRoot.name, "sitesaz", StringComparison.OrdinalIgnoreCase) ||
                _primaryRoot.path.EndsWith(Path.DirectorySeparatorChar + "sitesaz", StringComparison.OrdinalIgnoreCase) ||
                _primaryRoot.path.EndsWith(Path.AltDirectorySeparatorChar + "sitesaz", StringComparison.OrdinalIgnoreCase);
            if (looksLikeSitesaz)
            {
                ServerProfile sitesaz = _config.servers.FirstOrDefault(server =>
                    String.Equals(server.id, "sitesaz-pro", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(server.id, "production", StringComparison.OrdinalIgnoreCase));
                if (sitesaz != null) return sitesaz.id;
            }

            return null;
        }

        private ProcessResult RunServerCommand(ServerProfile server, string command, int timeoutSeconds)
        {
            string ssh = FindSsh();
            if (ssh == null) throw new FileNotFoundException("OpenSSH ssh.exe was not found in PATH.");
            string remote = "cd " + QuotePosix(server.workingDirectory.Trim()) + " && " + command;
            string sshArgs =
                "-o BatchMode=yes -o StrictHostKeyChecking=yes -p " + (server.port <= 0 ? 22 : server.port) + " " +
                (!String.IsNullOrWhiteSpace(server.identityFile) ? "-i " + QuoteArg(server.identityFile) + " " : String.Empty) +
                QuoteArg(server.user + "@" + server.host) + " " + QuoteArg(remote);
            return RunProcess(ssh, sshArgs, _primaryRoot.path, timeoutSeconds, 180000);
        }

        private static string NormalizeRemoteRelative(string value)
        {
            string path = (value ?? String.Empty).Trim().Replace('\\', '/').Trim('/');
            if (path == "." || String.IsNullOrWhiteSpace(path)) return String.Empty;
            if (path.StartsWith("/", StringComparison.Ordinal) || path.Contains("../") || path == ".." || path.Contains("/../"))
                throw new UnauthorizedAccessException("Remote path traversal is not allowed.");
            return path;
        }

        private static void EnsureRemotePathAllowed(string path)
        {
            string[] parts = (path ?? String.Empty).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(part => BlockedSegments.Contains(part)) ||
                parts.Any(part => part.Equals(".env", StringComparison.OrdinalIgnoreCase) || part.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)) ||
                BlockedExtensions.Contains(Path.GetExtension(path ?? String.Empty)))
                throw new UnauthorizedAccessException("Remote path is blocked or sensitive.");
        }

        private void EnsureExecutionAllowed()
        {
            if (!_config.executionEnabled)
                throw new UnauthorizedAccessException("Command execution is disabled in config.json.");
            if (!_executionGrantedForRequest)
                throw new UnauthorizedAccessException("Console and SSH permission was not granted by the extension toggle.");
        }

        private void EnsureServerExecutionAllowed()
        {
            if (!_config.executionEnabled)
                throw new UnauthorizedAccessException("Command execution is disabled in config.json.");
            if (!_serverExecutionGrantedForRequest)
                throw new UnauthorizedAccessException("Server execution permission was not granted by the extension toggle.");
        }

        private ProcessResult RunRg(string pattern, int resultLimit, WorkspaceRoot root)
        {
            ValidateRegex(pattern);
            string rg = FindExecutable("rg.exe");
            if (rg == null)
                throw new FileNotFoundException("ripgrep (rg.exe) is required but was not found in PATH.");

            string args = "-n -i --hidden --color never --no-heading --max-columns 260 --max-count 4 " +
                          "-g !.git/** -g !node_modules/** -g !vendor/** -g !storage/logs/** -g !bootstrap/cache/** " +
                          "-g !.env -g !.env.* -g !*.pem -g !*.key -g !*.pfx -g !*.p12 " +
                          QuoteArg(pattern) + " .";
            ProcessResult result = RunProcess(rg, args, root.path, _config.processTimeoutSeconds, 500000);
            result.Output = String.Join(Environment.NewLine, (result.Output ?? String.Empty)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Take(resultLimit));
            return result;
        }

        private ProcessResult RunRgAcrossRoots(string pattern, int resultLimit, string rootId)
        {
            List<WorkspaceRoot> roots = SelectRoots(rootId);
            StringBuilder output = new StringBuilder();
            StringBuilder error = new StringBuilder();
            int remaining = Math.Max(1, resultLimit);
            bool timedOut = false;
            int exitCode = 1;

            foreach (WorkspaceRoot root in roots)
            {
                if (remaining <= 0) break;
                ProcessResult part = RunRg(pattern, remaining, root);
                timedOut = timedOut || part.TimedOut;
                if (part.ExitCode == 0) exitCode = 0;
                if (!String.IsNullOrWhiteSpace(part.Error)) error.AppendLine("[" + root.id + "] " + part.Error.Trim());
                foreach (string line in (part.Output ?? String.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    output.AppendLine("[" + root.id + "] " + line);
                    remaining--;
                    if (remaining <= 0) break;
                }
            }

            return new ProcessResult
            {
                ExitCode = exitCode,
                Output = output.ToString(),
                Error = error.ToString(),
                TimedOut = timedOut
            };
        }

        private string GetProjectFiles(int limit, WorkspaceRoot root)
        {
            string rg = FindExecutable("rg.exe");
            if (rg == null)
                throw new FileNotFoundException("ripgrep (rg.exe) is required but was not found in PATH.");
            string args = "--files --hidden -g !.git/** -g !node_modules/** -g !vendor/** -g !storage/logs/** " +
                          "-g !bootstrap/cache/** -g !.env -g !.env.* -g !*.pem -g !*.key -g !*.pfx -g !*.p12";
            ProcessResult result = RunProcess(rg, args, root.path, _config.processTimeoutSeconds, 500000);
            EnsureSuccessOrNoMatches(result, "project tree");
            return String.Join(Environment.NewLine, (result.Output ?? String.Empty)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Take(limit));
        }

        private string GetProjectFilesAcrossRoots(int limit, string rootId)
        {
            StringBuilder files = new StringBuilder();
            int remaining = Math.Max(1, limit);
            foreach (WorkspaceRoot root in SelectRoots(rootId))
            {
                if (remaining <= 0) break;
                files.AppendLine("=== " + root.id + " ===");
                string part = GetProjectFiles(remaining, root);
                string[] lines = part.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    files.AppendLine(root.id + "/" + NormalizeRelative(line));
                    remaining--;
                    if (remaining <= 0) break;
                }
            }
            return files.ToString();
        }

        private string GetSnippet(WorkspaceRoot root, string relative, int start, int end)
        {
            string full = ResolveProjectPath(root, relative, true);
            string[] lines = ReadTextLines(full, _config.maxReadBytes * 8);
            start = Math.Max(1, start);
            end = Math.Min(lines.Length, end);
            StringBuilder text = new StringBuilder();
            text.AppendLine("--- " + root.id + "/" + NormalizeRelative(relative) + ":" + start + " ---");
            for (int i = start; i <= end; i++)
                text.AppendLine(i.ToString().PadLeft(6) + " | " + lines[i - 1]);
            return text.ToString();
        }

        private string CreateCheckpoint(WorkspaceRoot root, IEnumerable<string> paths)
        {
            return CreateCheckpoint(root, paths, new string[0]);
        }

        private string CreateCheckpoint(WorkspaceRoot root, IEnumerable<string> paths, IEnumerable<string> directoryPaths)
        {
            string id = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string directory = Path.Combine(_config.dataDirectory, "checkpoints", id);
            var existing = HistoryDirectories();
            long used = existing.Sum(d => new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length));
            long incoming = paths.Distinct(StringComparer.OrdinalIgnoreCase).Sum(p => { string f = ResolveProjectPath(root, p, false); return File.Exists(f) ? new FileInfo(f).Length : 0L; });
            if (existing.Count >= 50 || used + incoming > 200L * 1024 * 1024)
                throw new IOException("فضای تاریخچه پر شده است؛ برای ادامه تاریخچه را پاک کنید (سقف ۵۰ مرحله یا ۲۰۰ مگابایت).");
            Directory.CreateDirectory(directory);
            List<BackupEntry> entries = new List<BackupEntry>();
            foreach (string relative in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string full = ResolveProjectPath(root, relative, false);
                bool exists = File.Exists(full);
                entries.Add(new BackupEntry { root = root.id, path = NormalizeRelative(relative), existed = exists, isDirectory = false });
                if (exists)
                {
                    string backup = Path.Combine(directory, "files", root.id, NormalizeRelative(relative).Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    File.Copy(full, backup, true);
                }
            }
            foreach (string relative in directoryPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string full = ResolveProjectPath(root, relative, false);
                entries.Add(new BackupEntry
                {
                    root = root.id,
                    path = NormalizeRelative(relative),
                    existed = Directory.Exists(full),
                    isDirectory = true
                });
            }
            File.WriteAllText(Path.Combine(directory, "manifest.json"), _json.Serialize(entries), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "project.txt"), Path.GetFullPath(_primaryRoot.path), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "roots.json"), _json.Serialize(_roots.ToDictionary(r => r.id, r => Path.GetFullPath(r.path))), new UTF8Encoding(false));
            return id;
        }

        private List<string> HistoryDirectories()
        {
            string parent = Path.Combine(_config.dataDirectory, "checkpoints");
            if (!Directory.Exists(parent)) return new List<string>();
            return Directory.GetDirectories(parent).Where(d => File.Exists(Path.Combine(d, "project.txt")) &&
                String.Equals(File.ReadAllText(Path.Combine(d, "project.txt"), Encoding.UTF8), Path.GetFullPath(_primaryRoot.path), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).ToList();
        }

        private Dictionary<string, object> HistoryList()
        {
            var items = HistoryDirectories().Select(d => {
                var entries = _json.Deserialize<List<BackupEntry>>(File.ReadAllText(Path.Combine(d, "manifest.json"), Encoding.UTF8));
                return new { id = Path.GetFileName(d), createdAt = Directory.GetCreationTimeUtc(d).ToString("o"),
                    files = entries.Select(e => e.root + "/" + e.path).ToArray(),
                    bytes = new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) };
            }).ToArray();
            return new Dictionary<string, object> { { "items", items }, { "count", items.Length },
                { "bytes", items.Sum(i => i.bytes) }, { "cleanupRecommended", items.Length >= 10 } };
        }

        private Dictionary<string, object> HistoryClear()
        {
            foreach (string directory in HistoryDirectories()) Directory.Delete(directory, true);
            return HistoryList();
        }

        private Dictionary<string, object> HistoryRestore(Dictionary<string, object> args)
        {
            string id = RequireString(args, "checkpointId", 3, 120);
            var directories = HistoryDirectories();
            int index = directories.FindIndex(d => Path.GetFileName(d) == id);
            if (index < 0) throw new InvalidDataException("این مرحله متعلق به پروژه فعال نیست یا دیگر موجود نیست.");
            var selected = directories.Take(index + 1).ToList();
            // Validate every backup before touching the project. Keep a recovery copy for failed restores.
            var recovery = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string directory in selected) {
                ValidateCheckpointRoots(directory);
                var entries = _json.Deserialize<List<BackupEntry>>(File.ReadAllText(Path.Combine(directory, "manifest.json"), Encoding.UTF8));
                foreach (var entry in entries.Where(e => !e.isDirectory)) {
                    var root = ResolveRoot(entry.root, false);
                    string target = ResolveProjectPath(root, entry.path, false);
                    if (Directory.Exists(target)) throw new IOException("یک پوشه جای فایل را گرفته است: " + entry.path);
                    if (entry.existed && !File.Exists(Path.Combine(directory, "files", root.id, entry.path.Replace('/', Path.DirectorySeparatorChar))))
                        throw new IOException("نسخه پشتیبان ناقص است: " + entry.path);
                    if (!recovery.ContainsKey(target)) recovery[target] = File.Exists(target) ? File.ReadAllBytes(target) : null;
                }
            }
            try { foreach (string directory in selected) RestoreCheckpoint(Path.GetFileName(directory)); }
            catch {
                foreach (var pair in recovery) {
                    if (pair.Value == null) { if (File.Exists(pair.Key)) File.Delete(pair.Key); }
                    else { Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)); File.WriteAllBytes(pair.Key, pair.Value); }
                }
                throw;
            }
            foreach (string directory in selected) Directory.Delete(directory, true);
            return new Dictionary<string, object> { { "restoredSteps", selected.Count }, { "restoredFiles", recovery.Keys.ToArray() } };
        }

        private void ValidateCheckpointRoots(string directory)
        {
            var roots = _json.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(directory, "roots.json"), Encoding.UTF8));
            foreach (var pair in roots)
                if (!String.Equals(Path.GetFullPath(ResolveRoot(pair.Key, false).path), pair.Value, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("مسیر پروژه با تاریخچه مطابقت ندارد.");
        }

        private string[] RestoreCheckpoint(string id)
        {
            string checkpointRoot = Path.GetFullPath(Path.Combine(_config.dataDirectory, "checkpoints", id));
            string allowedRoot = Path.GetFullPath(Path.Combine(_config.dataDirectory, "checkpoints")) + Path.DirectorySeparatorChar;
            if (!checkpointRoot.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Checkpoint path escaped its allowed directory.");
            string manifestPath = Path.Combine(checkpointRoot, "manifest.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("Checkpoint was not found.", id);

            List<BackupEntry> entries = _json.Deserialize<List<BackupEntry>>(File.ReadAllText(manifestPath, Encoding.UTF8));
            foreach (BackupEntry entry in entries.Where(e => !e.isDirectory))
            {
                WorkspaceRoot root = ResolveRoot(String.IsNullOrWhiteSpace(entry.root) ? _primaryRoot.id : entry.root, false);
                string target = ResolveProjectPath(root, entry.path, false);
                if (entry.existed)
                {
                    string backup = Path.Combine(checkpointRoot, "files", root.id, entry.path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(backup))
                    {
                        // Backward compatibility with checkpoints created by version 1.0.
                        backup = Path.Combine(checkpointRoot, "files", entry.path.Replace('/', Path.DirectorySeparatorChar));
                    }
                    if (!File.Exists(backup))
                        throw new InvalidDataException("Checkpoint backup is incomplete: " + entry.path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(backup, target, true);
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            foreach (BackupEntry entry in entries.Where(e => e.isDirectory).OrderByDescending(e => e.path.Count(c => c == '/')))
            {
                if (entry.existed) continue;
                WorkspaceRoot root = ResolveRoot(String.IsNullOrWhiteSpace(entry.root) ? _primaryRoot.id : entry.root, false);
                string target = ResolveProjectPath(root, entry.path, false);
                if (Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any())
                    Directory.Delete(target, false);
            }
            return entries.Select(e => (String.IsNullOrWhiteSpace(e.root) ? _primaryRoot.id : e.root) + "/" + e.path).ToArray();
        }

        private List<string> GetPatchPaths(string patch)
        {
            List<string> paths = new List<string>();
            string oldPath = null;
            foreach (string rawLine in patch.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.StartsWith("--- "))
                {
                    oldPath = CleanPatchPath(line.Substring(4));
                }
                else if (line.StartsWith("+++ "))
                {
                    string newPath = CleanPatchPath(line.Substring(4));
                    string selected = newPath == "/dev/null" ? oldPath : newPath;
                    if (!String.IsNullOrWhiteSpace(selected) && selected != "/dev/null")
                        paths.Add(selected);
                }
            }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private string CleanPatchPath(string value)
        {
            string path = value.Split('\t')[0].Trim();
            if (path.StartsWith("\"") && path.EndsWith("\""))
                path = path.Substring(1, path.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            if (path.StartsWith("a/") || path.StartsWith("b/"))
                path = path.Substring(2);
            return NormalizeRelative(path);
        }

        private List<WorkspaceRoot> BuildWorkspaceRoots(BridgeConfig config, string projectRootOverride)
        {
            List<WorkspaceRoot> roots = new List<WorkspaceRoot>();
            string primaryPath = String.IsNullOrWhiteSpace(projectRootOverride)
                ? config.projectRoot
                : Path.GetFullPath(projectRootOverride);
            if (!Directory.Exists(primaryPath))
                throw new DirectoryNotFoundException("Selected project root does not exist: " + primaryPath);

            AddWorkspaceRoot(roots, Path.GetFileName(primaryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), primaryPath, true);

            bool usesConfiguredProject = String.Equals(primaryPath, config.projectRoot, StringComparison.OrdinalIgnoreCase);
            if (usesConfiguredProject && !String.IsNullOrWhiteSpace(config.workspaceFile))
            {
                WorkspaceFileModel workspace = _json.Deserialize<WorkspaceFileModel>(File.ReadAllText(config.workspaceFile, Encoding.UTF8));
                string baseDirectory = Path.GetDirectoryName(config.workspaceFile);
                foreach (WorkspaceFolderModel folder in (workspace == null || workspace.folders == null) ? new WorkspaceFolderModel[0] : workspace.folders)
                {
                    if (folder == null || String.IsNullOrWhiteSpace(folder.path)) continue;
                    string resolved = Path.IsPathRooted(folder.path)
                        ? Path.GetFullPath(folder.path)
                        : Path.GetFullPath(Path.Combine(baseDirectory, folder.path));
                    if (!Directory.Exists(resolved)) continue;
                    if (roots.Any(r => String.Equals(r.path, resolved, StringComparison.OrdinalIgnoreCase))) continue;
                    string displayName = String.IsNullOrWhiteSpace(folder.name)
                        ? Path.GetFileName(resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                        : folder.name.Trim();
                    AddWorkspaceRoot(roots, displayName, resolved, false);
                }
            }

            return roots;
        }

        private void AddWorkspaceRoot(List<WorkspaceRoot> roots, string displayName, string path, bool primary)
        {
            string full = Path.GetFullPath(path);
            string baseId = MakeRootId(displayName);
            if (String.IsNullOrWhiteSpace(baseId)) baseId = "root";
            string id = baseId;
            int suffix = 2;
            while (roots.Any(r => String.Equals(r.id, id, StringComparison.OrdinalIgnoreCase)))
                id = baseId + "-" + (suffix++).ToString();

            roots.Add(new WorkspaceRoot
            {
                id = id,
                name = displayName,
                path = full,
                pathWithSeparator = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                primary = primary
            });
        }

        private static string MakeRootId(string value)
        {
            string id = Regex.Replace((value ?? String.Empty).Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
            return id;
        }

        private List<WorkspaceRoot> SelectRoots(string rootId)
        {
            if (!String.IsNullOrWhiteSpace(rootId) && !String.Equals(rootId, "all", StringComparison.OrdinalIgnoreCase))
                return new List<WorkspaceRoot> { ResolveRoot(rootId, false) };

            // Avoid duplicate results for nested workspace folders when searching the whole workspace.
            return _roots.Where(candidate => !_roots.Any(other =>
                !Object.ReferenceEquals(candidate, other) &&
                candidate.path.StartsWith(other.pathWithSeparator, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        private WorkspaceRoot ResolveRoot(string rootId, bool allowAll)
        {
            if (String.IsNullOrWhiteSpace(rootId)) return _primaryRoot;
            if (String.Equals(rootId, "all", StringComparison.OrdinalIgnoreCase))
            {
                if (allowAll) return null;
                throw new InvalidDataException("A single workspace root is required for this operation.");
            }

            WorkspaceRoot root = _roots.FirstOrDefault(r =>
                String.Equals(r.id, rootId, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(r.name, rootId, StringComparison.OrdinalIgnoreCase));
            if (root == null)
                throw new InvalidDataException("Unknown workspace root: " + rootId + ". Available roots: " + String.Join(", ", _roots.Select(r => r.id)));
            return root;
        }

        private WorkspaceRoot ResolveRootForPath(ref string relative, string requestedRoot)
        {
            if (!String.IsNullOrWhiteSpace(requestedRoot))
                return ResolveRoot(requestedRoot, false);

            string normalized = NormalizeRelative(relative);
            int slash = normalized.IndexOf('/');
            if (slash > 0)
            {
                string prefix = normalized.Substring(0, slash);
                WorkspaceRoot prefixed = _roots.FirstOrDefault(r => String.Equals(r.id, prefix, StringComparison.OrdinalIgnoreCase));
                if (prefixed != null)
                {
                    relative = normalized.Substring(slash + 1);
                    return prefixed;
                }
            }
            relative = normalized;
            return _primaryRoot;
        }

        private string ResolveProjectPath(WorkspaceRoot root, string relative, bool mustBeFile)
        {
            if (root == null) throw new InvalidDataException("Workspace root is required.");
            if (String.IsNullOrWhiteSpace(relative))
                throw new InvalidDataException("A relative project path is required.");
            string normalized = NormalizeRelative(relative);
            if (Path.IsPathRooted(normalized) || normalized.Contains(":"))
                throw new UnauthorizedAccessException("Absolute paths are not allowed.");
            string[] segments = normalized.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(s => s == "." || s == ".."))
                throw new UnauthorizedAccessException("Path traversal is not allowed.");
            if (segments.Any(IsBlockedSegment) || BlockedExtensions.Contains(Path.GetExtension(normalized)))
                throw new UnauthorizedAccessException("Sensitive or dependency paths are blocked.");

            string full = Path.GetFullPath(Path.Combine(root.path, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root.pathWithSeparator, StringComparison.OrdinalIgnoreCase) && !String.Equals(full, root.path, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Path escaped the selected workspace root.");
            if (mustBeFile && !File.Exists(full))
                throw new FileNotFoundException("Project file was not found.", root.id + "/" + normalized);
            return full;
        }

        private static bool IsBlockedSegment(string segment)
        {
            if (BlockedSegments.Contains(segment)) return true;
            if (segment.Equals(".env", StringComparison.OrdinalIgnoreCase) || segment.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)) return true;
            if (segment.IndexOf("credential", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (segment.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private ProcessResult RunGit(WorkspaceRoot root, string arguments, int timeoutSeconds)
        {
            string git = FindExecutable("git.exe");
            if (git == null) throw new FileNotFoundException("git.exe was not found in PATH.");
            return RunProcess(git, arguments, root.path, timeoutSeconds, 600000);
        }

        private static ProcessResult RunProcess(string fileName, string arguments, string workingDirectory, int timeoutSeconds, int maxCharacters)
        {
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? String.Empty,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            StringBuilder output = new StringBuilder();
            StringBuilder error = new StringBuilder();
            object gate = new object();

            using (Process process = new Process())
            {
                process.StartInfo = info;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
                {
                    if (eventArgs.Data == null) return;
                    lock (gate) { if (output.Length < maxCharacters) output.AppendLine(eventArgs.Data); }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
                {
                    if (eventArgs.Data == null) return;
                    lock (gate) { if (error.Length < maxCharacters) error.AppendLine(eventArgs.Data); }
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                bool exited = process.WaitForExit(Math.Max(1, timeoutSeconds) * 1000);
                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    process.WaitForExit();
                }
                else
                {
                    process.WaitForExit();
                }
                return new ProcessResult
                {
                    ExitCode = exited ? process.ExitCode : -1,
                    Output = output.ToString(),
                    Error = error.ToString(),
                    TimedOut = !exited
                };
            }
        }

        private static string FindExecutable(string name)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
            foreach (string directory in path.Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        private static string QuoteArg(string value)
        {
            if (value == null) return "\"\"";
            if (value.Length > 0 && !Regex.IsMatch(value, "[\\s\"]")) return value;
            StringBuilder result = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                }
                else if (character == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                }
                else
                {
                    result.Append('\\', backslashes);
                    result.Append(character);
                    backslashes = 0;
                }
            }
            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }

        private static void EnsureSuccess(ProcessResult result, string operation)
        {
            if (result.TimedOut) throw new TimeoutException(operation + " timed out.");
            if (result.ExitCode != 0) throw new InvalidOperationException(operation + " failed: " + Truncate(result.Error, 3000));
        }

        private static void EnsureSuccessOrNoMatches(ProcessResult result, string operation)
        {
            if (result.TimedOut) throw new TimeoutException(operation + " timed out.");
            if (result.ExitCode != 0 && result.ExitCode != 1) throw new InvalidOperationException(operation + " failed: " + Truncate(result.Error, 3000));
        }

        private static void ValidateRegex(string pattern)
        {
            if (pattern.Length > 2000) throw new InvalidDataException("Search pattern is too long.");
            try { new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
            catch (Exception ex) { throw new InvalidDataException("Search pattern is invalid: " + ex.Message); }
        }

        private static string[] ReadTextLines(string path, int maximumBytes)
        {
            byte[] bytes;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length > maximumBytes) throw new InvalidOperationException("File exceeds the configured read limit.");
                bytes = new byte[stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) break;
                    offset += count;
                }
            }
            if (bytes.Take(Math.Min(bytes.Length, 4096)).Any(b => b == 0))
                throw new InvalidDataException("Binary files cannot be read through the bridge.");
            string text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Replace('\r', '\n');
            return text.Split(new[] { '\n' });
        }

        private IEnumerable<string> ExtractTerms(string prompt)
        {
            return Regex.Matches(prompt ?? String.Empty, "[\\p{L}\\p{N}_./\\\\-]{3,}")
                .Cast<Match>()
                .Select(m => m.Value.Trim('.', '/', '\\', '-'))
                .Where(s => s.Length >= 3 && s.Length <= 80 && !StopWords.Contains(s))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeRelative(string path)
        {
            return (path ?? String.Empty).Trim().Replace('\\', '/').TrimStart('/');
        }

        private static string Truncate(string value, int maximumCharacters)
        {
            if (String.IsNullOrEmpty(value) || value.Length <= maximumCharacters) return value ?? String.Empty;
            return value.Substring(0, maximumCharacters) + Environment.NewLine + "[truncated by Project Bridge]";
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
        {
            object value;
            if (source != null && source.TryGetValue(key, out value))
            {
                Dictionary<string, object> dictionary = value as Dictionary<string, object>;
                if (dictionary != null) return dictionary;
            }
            return new Dictionary<string, object>();
        }

        private static object[] GetObjectArray(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null)
                return new object[0];

            object[] array = value as object[];
            if (array != null)
                return array;

            System.Collections.ArrayList list = value as System.Collections.ArrayList;
            if (list != null)
                return list.Cast<object>().ToArray();

            throw new InvalidDataException(key + " must be an array.");
        }

        private static string FindPowerShell()
        {
            return FindExecutable("pwsh.exe") ?? FindExecutable("powershell.exe");
        }

        private static string FindSsh()
        {
            return FindExecutable("ssh.exe");
        }

        private static string QuotePosix(string value)
        {
            return "'" + (value ?? String.Empty).Replace("'", "'\"'\"'") + "'";
        }

        private static string GetString(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : null;
        }

        private static string RequireString(Dictionary<string, object> source, string key, int minLength, int maxLength)
        {
            string value = GetString(source, key);
            if (String.IsNullOrWhiteSpace(value) || value.Length < minLength) throw new InvalidDataException(key + " is required.");
            if (value.Length > maxLength) throw new InvalidDataException(key + " exceeds its allowed length.");
            return value;
        }

        private static bool GetBool(Dictionary<string, object> source, string key, bool fallback)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return fallback;
            bool parsed;
            return Boolean.TryParse(Convert.ToString(value), out parsed) ? parsed : fallback;
        }

        private static int GetInt(Dictionary<string, object> source, string key, int fallback, int minimum, int maximum)
        {
            object value;
            int parsed;
            if (source == null || !source.TryGetValue(key, out value) || value == null || !Int32.TryParse(Convert.ToString(value), out parsed)) return fallback;
            return Math.Max(minimum, Math.Min(maximum, parsed));
        }

    }
}

using System;
using System.IO;
using System.Windows.Forms;

namespace HapticsBridge
{
    /// <summary>
    /// Finds the Silksong install folder: from this exe's own location (the
    /// bridge normally lives inside BepInEx\plugins), from a remembered path,
    /// or by asking the user. User-typed paths are accepted in any lazy
    /// format: unquoted, quoted, single-quoted, trailing slashes, forward
    /// slashes, env vars, a path to the .exe, or a parent folder.
    /// </summary>
    internal static class GameLocator
    {
        private const string GameExe = "Hollow Knight Silksong.exe";
        private static readonly string RememberFile =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gamepath.txt");

        public static string Locate(bool interactive)
        {
            // 1. Walk up from our own location (normal install layout).
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, GameExe)))
                    return dir.FullName;
                dir = dir.Parent;
            }

            // 2. Remembered path from a previous prompt.
            try
            {
                if (File.Exists(RememberFile))
                {
                    string saved = Validate(CleanUserPath(File.ReadAllText(RememberFile)));
                    if (saved != null) return saved;
                }
            }
            catch { }

            // 3. Common Steam locations.
            string[] guesses =
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight Silksong",
                @"C:\Program Files\Steam\steamapps\common\Hollow Knight Silksong",
            };
            foreach (var g in guesses)
            {
                string v = Validate(g);
                if (v != null) return v;
            }

            if (!interactive)
                return null;

            // 4. Ask the user.
            return PromptUser();
        }

        /// <summary>Normalize whatever the user pasted into a usable directory path.</summary>
        public static string CleanUserPath(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Trim();
            // Strip any combination of stray quotes/whitespace at both ends,
            // including unmatched ones ("C:\Games\Silksong  or  C:\Games\Silksong").
            s = s.Trim('"', '\'', ' ', '\t', '\r', '\n');
            if (s.Length == 0) return null;
            s = Environment.ExpandEnvironmentVariables(s);
            s = s.Replace('/', '\\');
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                try { s = Path.GetDirectoryName(s); } catch { return null; }
            }
            if (s == null) return null;
            s = s.TrimEnd('\\', ' ');
            return s.Length == 0 ? null : s;
        }

        /// <summary>Accepts the game root, its parent, or the _Data folder; returns the game root.</summary>
        public static string Validate(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                if (File.Exists(Path.Combine(dir, GameExe)))
                    return dir;
                string sub = Path.Combine(dir, "Hollow Knight Silksong");
                if (File.Exists(Path.Combine(sub, GameExe)))
                    return sub;
                if (dir.EndsWith("Hollow Knight Silksong_Data", StringComparison.OrdinalIgnoreCase))
                {
                    string parent = Path.GetDirectoryName(dir);
                    if (parent != null && File.Exists(Path.Combine(parent, GameExe)))
                        return parent;
                }
            }
            catch { }
            return null;
        }

        private static string PromptUser()
        {
            using (var form = new Form())
            {
                form.Text = "Silksong Haptics - locate game";
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.ClientSize = new System.Drawing.Size(560, 130);

                var label = new Label
                {
                    Text = "Couldn't find Hollow Knight: Silksong automatically.\nPaste your Silksong install folder below (any format is fine), or Browse.",
                    Location = new System.Drawing.Point(12, 10),
                    Size = new System.Drawing.Size(536, 36),
                };
                var box = new TextBox { Location = new System.Drawing.Point(12, 52), Size = new System.Drawing.Size(440, 24) };
                var browse = new Button { Text = "Browse...", Location = new System.Drawing.Point(460, 50), Size = new System.Drawing.Size(88, 26) };
                var ok = new Button { Text = "OK", Location = new System.Drawing.Point(372, 90), Size = new System.Drawing.Size(80, 28), DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Location = new System.Drawing.Point(460, 90), Size = new System.Drawing.Size(88, 28), DialogResult = DialogResult.Cancel };
                browse.Click += delegate
                {
                    using (var fbd = new FolderBrowserDialog { Description = "Select the Hollow Knight Silksong folder" })
                    {
                        if (fbd.ShowDialog(form) == DialogResult.OK)
                            box.Text = fbd.SelectedPath;
                    }
                };
                form.Controls.AddRange(new Control[] { label, box, browse, ok, cancel });
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                while (true)
                {
                    if (form.ShowDialog() != DialogResult.OK)
                        return null;
                    string cleaned = CleanUserPath(box.Text);
                    string valid = Validate(cleaned);
                    if (valid != null)
                    {
                        try { File.WriteAllText(RememberFile, valid); } catch { }
                        return valid;
                    }
                    MessageBox.Show(
                        string.Format("Couldn't find \"{0}\" under:\n{1}\n\nPlease check the path and try again.", GameExe, cleaned ?? "(empty)"),
                        "Not quite");
                }
            }
        }
    }
}

// Windows notifications (the notification center's pop-ups), through WinRT from Windows' own metadata (build.js REFS):
// no SDK and no package. An unpackaged app shows them under an AppUserModelID registered for this user
// (HKCU\Software\Classes\AppUserModelId\<id>: its name and logo). Clicks come back while the app runs (Activated).
// Show returns false when Windows refuses (older Windows, notifications off): callers fall back to a tray balloon.
using System;
using System.IO;
using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Glide
{
    public static class Toasts
    {
        public static string Id { get { return "1949-2005." + Program.Name; } }

        // A click on a notification or one of its buttons: its arguments ("open", "folder", "show"; then the download's id)
        public static event Action<string, string> Activated;

        static bool registered;

        static void Register()
        {
            if (registered) return;
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Id))
            {
                k.SetValue("DisplayName", Program.Name);
                var logo = Path.Combine(Program.Folder, "assets", "logo.png");
                if (File.Exists(logo)) k.SetValue("IconUri", logo);
            }
            registered = true;
        }

        static string X(string s) { return SecurityElement.Escape(s ?? ""); }

        // --check-notification: one notification, found in the notification center, then removed from it. "ok" or why not
        public static string Check()
        {
            try
            {
                Register();
                var xml = new XmlDocument();
                xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>" + X(Program.Name) + "</text><text>Notifications work.</text></binding></visual></toast>");
                var toast = new ToastNotification(xml) { Tag = "check", Group = "check" };
                var notifier = ToastNotificationManager.CreateToastNotifier(Id);
                if (notifier.Setting != NotificationSetting.Enabled) return "off: " + notifier.Setting;
                notifier.Show(toast);
                System.Threading.Thread.Sleep(1000);
                var found = false;
                foreach (var n in ToastNotificationManager.History.GetHistory(Id)) if (n.Tag == "check") found = true;
                ToastNotificationManager.History.Remove("check", "check", Id);
                return found ? "ok" : "shown, but not in the notification center (notifications may be off for " + Program.Name + ")";
            }
            catch (Exception e) { return "failed: " + e.Message; }
        }

        // title and text; buttons: label → action, each sent back with id through Activated (a click on the body: "show")
        public static bool Show(string title, string text, string id, params string[][] buttons)
        {
            try
            {
                Register();
                var actions = "";
                foreach (var b in buttons)
                    actions += "<action content='" + X(b[0]) + "' arguments='" + X(b[1] + "|" + id) + "' activationType='foreground'/>";
                var xml = new XmlDocument();
                xml.LoadXml("<toast launch='" + X("show|" + id) + "'><visual><binding template='ToastGeneric'><text>" + X(title) + "</text><text>" + X(text) +
                    "</text></binding></visual>" + (actions != "" ? "<actions>" + actions + "</actions>" : "") + "</toast>");
                var toast = new ToastNotification(xml);
                toast.Activated += (s, e) =>
                {
                    var a = e as ToastActivatedEventArgs;
                    var parts = (a != null ? a.Arguments : "show|" + id).Split(new[] { '|' }, 2);
                    var h = Activated;
                    if (h != null) h(parts[0], parts.Length > 1 ? parts[1] : null);
                };
                var notifier = ToastNotificationManager.CreateToastNotifier(Id);
                if (notifier.Setting != NotificationSetting.Enabled) return false; // the user turned them off: theirs to decide
                notifier.Show(toast);
                return true;
            }
            catch (Exception e)
            {
                Log.Write("app", "notification failed: " + e.Message);
                return false;
            }
        }
    }
}

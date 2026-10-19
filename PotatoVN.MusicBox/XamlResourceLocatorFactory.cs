using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace PotatoVN.MusicBox
{
    /// <summary>
    /// Locates XAML resources inside the plugin package. Always use
    /// <see cref="PluginControlInit"/> instead of the generated
    /// <c>InitializeComponent()</c>, because the plugin is loaded from an
    /// arbitrary folder rather than the host's ms-appx root.
    /// </summary>
    internal static class XamlResourceLocatorFactory
    {
        private static readonly string PackageName;
        public static string PackagePath = string.Empty;

        static XamlResourceLocatorFactory()
        {
            PackageName = typeof(XamlResourceLocatorFactory).Assembly.GetName().Name
                          ?? throw new InvalidOperationException();
        }

        internal static Uri Create([CallerFilePath] string callerFilePath = "")
        {
            var i = callerFilePath.LastIndexOf("Stamped", StringComparison.Ordinal) + 8;
            var componentPath = callerFilePath[i..^3];
            return new Uri($"ms-appx:///{PackagePath}\\{PackageName}\\{componentPath}");
        }

        internal static Uri CreateFromRelativePath(string relativePath)
        {
            relativePath = relativePath.Replace('/', '\\').TrimStart('\\');
            return new Uri($"ms-appx:///{PackagePath}\\{PackageName}\\{relativePath}");
        }

        internal static void PluginControlInit(ref bool contentLoaded, object ctrl,
            [CallerFilePath] string callerFilePath = "")
        {
            if (contentLoaded) return;
            contentLoaded = true;

            Application.LoadComponent(ctrl, Create(callerFilePath), ComponentResourceLocation.Application);
        }
    }
}

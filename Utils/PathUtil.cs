using System;
using System.IO;

namespace AtomizeJs.Utils
{
    public static class PathUtil
    {
        // Compute POSIX-style relative path from fromPath to toPath
        public static string Rel(string fromPath, string toPath)
        {
            if (string.IsNullOrEmpty(fromPath)) fromPath = Directory.GetCurrentDirectory();
            var fromUri = new Uri(Path.GetFullPath(fromPath) + Path.DirectorySeparatorChar);
            var toUri = new Uri(Path.GetFullPath(toPath));
            var rel = Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString());
            // Convert to POSIX separators
            rel = rel.Replace(Path.DirectorySeparatorChar, '/');
            if (!rel.StartsWith("./") && !rel.StartsWith("../")) rel = "./" + rel;
            return rel;
        }
    }
}

using System;

namespace AtomizeJs.Core
{
    public interface ILogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    public class ConsoleLogger : ILogger
    {
        public void Info(string message) => Console.WriteLine(message);
        public void Warn(string message) => Console.WriteLine(message);
        public void Error(string message) => Console.WriteLine(message);
    }

    public static class Logger
    {
        private static ILogger _current = new ConsoleLogger();
        public static void SetLogger(ILogger logger) { if (logger != null) _current = logger; }
        public static void Info(string message) => _current.Info(message);
        public static void Warn(string message) => _current.Warn(message);
        public static void Error(string message) => _current.Error(message);
    }
}

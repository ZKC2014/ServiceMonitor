using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace ServiceMonitor
{
    internal static class Program
    {
        private static volatile bool _stopRequested;

        private static int Main()
        {
            var settings = Settings.Load();
            var log = new Logger(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServiceMonitor.log"));

            if (settings.ServicesToWatch.Count == 0)
            {
                log.Error("No services configured. Set ServicesToWatch in App.config.");
                return 1;
            }

            // Make sure SMTP servers that require TLS 1.2 can be reached on .NET Framework.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var notifier = new EmailNotifier(settings, log);
            var watcher = new ServiceWatcher(settings, notifier, log);

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                _stopRequested = true;
            };

            log.Info("Monitoring " + string.Join(", ", settings.ServicesToWatch) +
                     " every " + settings.PollIntervalSeconds + " second(s). Press Ctrl+C to stop.");

            while (!_stopRequested)
            {
                watcher.Poll();
                SleepUnlessStopped(settings.PollIntervalSeconds);
            }

            log.Info("Monitor stopped.");
            return 0;
        }

        // Sleep in half-second slices so Ctrl+C is handled promptly.
        private static void SleepUnlessStopped(int seconds)
        {
            var slices = seconds * 2;
            for (var i = 0; i < slices && !_stopRequested; i++)
            {
                Thread.Sleep(500);
            }
        }
    }

    internal sealed class Settings
    {
        public List<string> ServicesToWatch { get; private set; }
        public int PollIntervalSeconds { get; private set; }
        public bool AlertOnStartupIfStopped { get; private set; }
        public bool AlertOnRecovery { get; private set; }
        public bool SendEmail { get; private set; }
        public string SmtpHost { get; private set; }
        public int SmtpPort { get; private set; }
        public bool SmtpUseSsl { get; private set; }
        public string SmtpUser { get; private set; }
        public string SmtpPassword { get; private set; }
        public string EmailFrom { get; private set; }
        public List<string> EmailTo { get; private set; }
        public string SubjectPrefix { get; private set; }

        public static Settings Load()
        {
            return new Settings
            {
                ServicesToWatch = SplitList(Read("ServicesToWatch", null)),
                PollIntervalSeconds = Math.Max(5, ReadInt("PollIntervalSeconds", 30)),
                AlertOnStartupIfStopped = ReadBool("AlertOnStartupIfStopped", true),
                AlertOnRecovery = ReadBool("AlertOnRecovery", false),
                SendEmail = ReadBool("SendEmail", true),
                SmtpHost = Read("SmtpHost", null),
                SmtpPort = ReadInt("SmtpPort", 587),
                SmtpUseSsl = ReadBool("SmtpUseSsl", true),
                SmtpUser = Read("SmtpUser", null),
                SmtpPassword = Read("SmtpPassword", null),
                EmailFrom = Read("EmailFrom", null),
                EmailTo = SplitList(Read("EmailTo", null)),
                SubjectPrefix = ConfigurationManager.AppSettings["SubjectPrefix"] ?? "[ServiceMonitor] "
            };
        }

        private static string Read(string key, string fallback)
        {
            var value = ConfigurationManager.AppSettings[key];
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static int ReadInt(string key, int fallback)
        {
            int result;
            return int.TryParse(Read(key, null), out result) ? result : fallback;
        }

        private static bool ReadBool(string key, bool fallback)
        {
            bool result;
            return bool.TryParse(Read(key, null), out result) ? result : fallback;
        }

        private static List<string> SplitList(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return new List<string>();
            }

            return value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(item => item.Trim())
                        .Where(item => item.Length > 0)
                        .ToList();
        }
    }

    internal sealed class Logger
    {
        private readonly string _path;
        private readonly object _sync = new object();

        public Logger(string path)
        {
            _path = path;
        }

        public void Info(string message)
        {
            Write("INFO ", message);
        }

        public void Warn(string message)
        {
            Write("WARN ", message);
        }

        public void Error(string message)
        {
            Write("ERROR", message);
        }

        private void Write(string level, string message)
        {
            var line = string.Format("{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}", DateTime.Now, level, message);
            Console.WriteLine(line);

            lock (_sync)
            {
                try
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
                catch (IOException)
                {
                    // Log file locked by another process; console output is still shown.
                }
                catch (UnauthorizedAccessException)
                {
                    // No write permission next to the executable; console output is still shown.
                }
            }
        }
    }

    internal sealed class EmailNotifier
    {
        private readonly Settings _settings;
        private readonly Logger _log;

        public EmailNotifier(Settings settings, Logger log)
        {
            _settings = settings;
            _log = log;
        }

        public void Send(string subject, string body)
        {
            if (!_settings.SendEmail || _settings.EmailTo.Count == 0 || string.IsNullOrEmpty(_settings.SmtpHost))
            {
                _log.Warn("Email not sent (disabled or not configured): " + subject);
                return;
            }

            try
            {
                using (var message = new MailMessage())
                {
                    message.From = new MailAddress(_settings.EmailFrom);
                    foreach (var recipient in _settings.EmailTo)
                    {
                        message.To.Add(recipient);
                    }

                    message.Subject = _settings.SubjectPrefix + subject;
                    message.Body = body;
                    message.IsBodyHtml = false;

                    using (var client = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort))
                    {
                        client.EnableSsl = _settings.SmtpUseSsl;
                        client.Timeout = 30000;

                        if (!string.IsNullOrEmpty(_settings.SmtpUser))
                        {
                            client.Credentials = new NetworkCredential(_settings.SmtpUser, _settings.SmtpPassword);
                        }

                        client.Send(message);
                    }
                }

                _log.Info("Alert emailed: " + subject);
            }
            catch (Exception ex)
            {
                _log.Error("Failed to send alert email '" + subject + "': " + ex.Message);
            }
        }
    }

    internal sealed class ServiceWatcher
    {
        private readonly Settings _settings;
        private readonly EmailNotifier _notifier;
        private readonly Logger _log;
        private readonly Dictionary<string, ServiceControllerStatus> _lastStatus =
            new Dictionary<string, ServiceControllerStatus>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unreadable =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ServiceWatcher(Settings settings, EmailNotifier notifier, Logger log)
        {
            _settings = settings;
            _notifier = notifier;
            _log = log;
        }

        public void Poll()
        {
            foreach (var serviceName in _settings.ServicesToWatch)
            {
                ServiceControllerStatus status;
                string displayName;
                string error;

                if (!TryReadStatus(serviceName, out status, out displayName, out error))
                {
                    // Alert once per outage rather than on every poll.
                    if (_unreadable.Add(serviceName))
                    {
                        _log.Error(serviceName + ": unable to read status - " + error);
                        Alert(serviceName, serviceName, "Unable to query service",
                              "The monitor could not read this service's status.\r\nError: " + error);
                    }
                    continue;
                }

                if (_unreadable.Remove(serviceName))
                {
                    _log.Info(serviceName + ": status readable again (" + status + ").");
                }

                ServiceControllerStatus previous;
                if (!_lastStatus.TryGetValue(serviceName, out previous))
                {
                    // First successful reading: establish the baseline.
                    _lastStatus[serviceName] = status;
                    _log.Info(serviceName + " initial status: " + status);

                    if (status == ServiceControllerStatus.Stopped && _settings.AlertOnStartupIfStopped)
                    {
                        Alert(serviceName, displayName, "Service is STOPPED",
                              "The service was found stopped when monitoring started.");
                    }
                    continue;
                }

                if (status == previous)
                {
                    continue;
                }

                _log.Info(serviceName + " status changed: " + previous + " -> " + status);
                _lastStatus[serviceName] = status;

                if (status == ServiceControllerStatus.Stopped)
                {
                    Alert(serviceName, displayName, "Service has STOPPED",
                          "Status changed from " + previous + " to Stopped.");
                }
                else if (status == ServiceControllerStatus.Running &&
                         previous == ServiceControllerStatus.Stopped &&
                         _settings.AlertOnRecovery)
                {
                    Alert(serviceName, displayName, "Service is RUNNING again",
                          "Status changed from Stopped to Running.");
                }
            }
        }

        private void Alert(string serviceName, string displayName, string headline, string details)
        {
            var subject = headline + ": " + serviceName + " on " + Environment.MachineName;

            var body = new StringBuilder();
            body.AppendLine("Machine : " + Environment.MachineName);
            body.AppendLine("Service : " + serviceName + " (" + displayName + ")");
            body.AppendLine("Time    : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            body.AppendLine();
            body.AppendLine(details);

            _log.Warn(subject);
            _notifier.Send(subject, body.ToString());
        }

        private static bool TryReadStatus(string serviceName, out ServiceControllerStatus status,
                                          out string displayName, out string error)
        {
            status = default(ServiceControllerStatus);
            displayName = serviceName;
            error = null;

            try
            {
                // Accessing Status throws InvalidOperationException if the service does not exist.
                using (var controller = new ServiceController(serviceName))
                {
                    status = controller.Status;
                    displayName = controller.DisplayName;
                }
                return true;
            }
            catch (InvalidOperationException ex)
            {
                error = ex.Message;
            }
            catch (Win32Exception ex)
            {
                error = ex.Message;
            }

            return false;
        }
    }
}

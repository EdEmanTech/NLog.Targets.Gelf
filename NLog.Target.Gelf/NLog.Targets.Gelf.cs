﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NLog.Targets
{
    [NLog.Targets.Target("Gelf")]
    public class Gelf : NLog.Targets.Target
    {
        #region Private Members
        // test
        private const int _shortMessageLength = 250;
        private const string _gelfVersion = "1.0";
        private int _maxHeaderSize
        {
            get
            {
                switch(GraylogVersion)
                {
                    case "0.9.7":
                        return 8;
                    default: // Default to version "0.9.5".
                        return 32;
                }
            }
        }

        #endregion

        #region Public properties

        public string GelfServer { get; set; }
        public int Port { get; set; }
        public string Sender { get; set; }
        public string Facility { get; set; }
        public int MaxChunkSize { get; set; }
        public string GraylogVersion { get; set; }

        #endregion

        #region Public Constructors

        public Gelf()
        {
            GelfServer = "127.0.0.1";
            Port = 12201;
            Sender = Assembly.GetCallingAssembly().GetName().Name;
            Facility = null;
            MaxChunkSize = 1024;
        }

        #endregion

        #region Overridden NLog methods

        /// <summary>
        /// This is where we hook into NLog, by overriding the Write method. 
        /// </summary>
        /// <param name="logEvent">The NLog.LogEventInfo </param>
        protected override void Write(LogEventInfo logEvent)
        {
            // Store the current UI culture
            CultureInfo currentCulture = Thread.CurrentThread.CurrentCulture;
            // Set the current Locale to "en-GB" for proper date formatting
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-GB");

            string message = CreateGelfJsonFromLoggingEvent(logEvent.FormattedMessage, logEvent.Level);
            try
            {
                SendMessage(GelfServer, Port, message);
            }
            catch (Exception e)
            {
                // If there's an error then log the message.
                string errorMessage = CreateGelfJsonFromLoggingEvent(e.ToString(), LogLevel.Fatal);
                SendMessage(GelfServer, Port, errorMessage);
            }

            // Restore the original culture
            Thread.CurrentThread.CurrentCulture = currentCulture;
        }

        #endregion

        #region Private Methods

        private void SendMessage(string gelfServer, int serverPort, string message)
        {
            string ipAddress = Dns.GetHostAddresses(gelfServer).FirstOrDefault().ToString();
            using (var udpClient = new UdpClient(ipAddress, serverPort))
            {
                var gzipMessage = GzipMessage(message);
                if (gzipMessage.Length > MaxChunkSize)
                {
                    var chunkCount = (gzipMessage.Length / MaxChunkSize) + 1;
                    if (chunkCount > 127)
                        throw new InvalidOperationException("The number of chunks to send was greater than 127.");

                    var messageId = GenerateMessageId(gelfServer);
                    for (int i = 0; i < chunkCount; i++)
                    {
                        var messageChunkPrefix = CreateChunkedMessagePart(messageId, i, chunkCount);
                        var skip = i*MaxChunkSize;
                        var messageChunkSuffix = gzipMessage.Skip(skip).Take(MaxChunkSize).ToArray<byte>();

                        var messageChunkFull = new byte[messageChunkPrefix.Length + messageChunkSuffix.Length];
                        messageChunkPrefix.CopyTo(messageChunkFull, 0);
                        messageChunkSuffix.CopyTo(messageChunkFull, messageChunkPrefix.Length);

                        udpClient.Send(messageChunkFull, messageChunkFull.Length);
                    }
                }
                else
                {
                    udpClient.Send(gzipMessage, gzipMessage.Length);
                }
            }
        }

        private byte[] GzipMessage(String message)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(message);
            var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.GZipStream(ms, CompressionMode.Compress, true))
            {
                zip.Write(buffer, 0, buffer.Length);
            }
            ms.Position = 0;
            var compressed = new byte[ms.Length];
            ms.Read(compressed, 0, compressed.Length);
            return compressed;
        }

        private int GetGelfSeverity(LogLevel logLevel)
        {
            GelfSeverity logLevelToReturn = GelfSeverity.Notice;

            if (logLevel == LogLevel.Fatal)
                logLevelToReturn = GelfSeverity.Emergency;
            else if (logLevel == LogLevel.Error)
                logLevelToReturn = GelfSeverity.Error;
            else if (logLevel == LogLevel.Warn)
                logLevelToReturn = GelfSeverity.Warning;
            else if (logLevel == LogLevel.Info)
                logLevelToReturn = GelfSeverity.Informational;
            else if (logLevel == LogLevel.Debug)
                logLevelToReturn = GelfSeverity.Debug;
            else if (logLevel == LogLevel.Trace)
                logLevelToReturn = GelfSeverity.Notice;

            return (int)logLevelToReturn;
        }

        private string CreateGelfJsonFromLoggingEvent(string body, LogLevel level)
        {
            var fullMessage = body;
            var shortMessage = fullMessage.Length > _shortMessageLength ? fullMessage.Substring(0, _shortMessageLength - 1) : fullMessage;
            string machine = System.Net.Dns.GetHostName();

            var gelfMessage = new GelfMessage
            {
                Facility = Facility ?? "GELF",
                File = "",
                FullMessage = fullMessage,
                Host = machine,
                Level = GetGelfSeverity(level),
                Line = "",
                ShortMessage = shortMessage,
                TimeStamp = DateTime.Now,
                Version = _gelfVersion
            };

            return JsonConvert.SerializeObject(gelfMessage);
        }

        private byte[] CreateChunkedMessagePart(string messageId, int chunkNumber, int chunkCount)
        {
            var result = new List<byte>();

            //Chunked GELF ID: 0x1e 0x0f (identifying this message as a chunked GELF message)
            result.Add(Convert.ToByte(30));
            result.Add(Convert.ToByte(15));

            //Message ID: 32 bytes
            result.AddRange(Encoding.Default.GetBytes(messageId).ToArray<byte>());

            result.AddRange(GetChunkPart(chunkNumber, chunkCount));

            return result.ToArray<byte>();
        }

        private byte[] GetChunkPart(int chunkNumber, int chunkCount)
        {
            var bytes = new List<byte>();

            if (GraylogVersion != "0.9.6")
                bytes.Add(Convert.ToByte(0));

            bytes.Add(Convert.ToByte(chunkNumber));

            if (GraylogVersion != "0.9.6")
                bytes.Add(Convert.ToByte(0));

            bytes.Add(Convert.ToByte(chunkCount));

            return bytes.ToArray();
        }

        private string GenerateMessageId(string serverHostName)
        {
            var md5String = String.Join("", MD5.Create().ComputeHash(Encoding.Default.GetBytes(serverHostName)).Select(it => it.ToString("x2")).ToArray<string>());
            var random = new Random((int)DateTime.Now.Ticks);
            var sb = new StringBuilder();
            var t = DateTime.Now.Ticks % 1000000000;
            var s = String.Format("{0}{1}", md5String.Substring(0, 10), md5String.Substring(20, 10));
            var r = random.Next(10000000).ToString("00000000");

            sb.Append(t);
            sb.Append(s);
            sb.Append(r);

            //Message ID: 32 bytes
            return sb.ToString().Substring(0, _maxHeaderSize);
        }

        #endregion

        #region Public Enums

        public enum GelfSeverity
        {
            Emergency = 0,
            Alert = 1,
            Critical = 2,
            Error = 3,
            Warning = 4,
            Notice = 5,
            Informational = 6,
            Debug = 7
        };

        #endregion
    }

    [JsonObject(MemberSerialization.OptIn)]
    class GelfMessage
    {
        [JsonProperty("facility")]
        public string Facility { get; set; }

        [JsonProperty("file")]
        public string File { get; set; }

        [JsonProperty("full_message")]
        public string FullMessage { get; set; }

        [JsonProperty("host")]
        public string Host { get; set; }

        [JsonProperty("level")]
        public int Level { get; set; }

        [JsonProperty("line")]
        public string Line { get; set; }

        [JsonProperty("short_message")]
        public string ShortMessage { get; set; }

        [JsonProperty("timestamp")]
        public DateTime TimeStamp { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }
    }
}

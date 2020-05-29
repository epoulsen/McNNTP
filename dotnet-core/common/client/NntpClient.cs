﻿namespace mcnntp.common.client
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Threading.Tasks;
    using mcnntp.common;

    public class NntpClient
    {
        public bool CanPost { get; private set; }

        private Connection Connection { get; set; }

        public int Port { get; set; }

        /// <summary>
        /// Gets the newsgroup currently selected by this connection
        /// </summary>
        public string CurrentNewsgroup { get; private set; }

        /// <summary>
        /// Gets the article number currently selected by this connection for the selected newsgroup
        /// </summary>
        public long? CurrentArticleNumber { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="NntpClient"/> class.
        /// </summary>
        public NntpClient()
        {
            // Establish default values
            this.CanPost = true;
            this.Port = 119;
        }

        #region Connections
        public async Task<bool> ConnectAsync(string hostName, bool? tls = null)
        {
            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(hostName, this.Port);
            Stream stream;

            if (tls ?? this.Port == 563)
            {
                var sslStream = new SslStream(tcpClient.GetStream());
                await sslStream.AuthenticateAsClientAsync(hostName);
                stream = sslStream;
            }
            else
                stream = tcpClient.GetStream();

            this.Connection = new Connection(tcpClient, stream);

            var response = await this.Connection.Receive();

            switch (response.Code)
            {
                case 200:
                    this.CanPost = true;
                    return true;
                case 201:
                    this.CanPost = false;
                    return true;
                default:
                    throw new NntpException(string.Format("Unexpected response code {0}.  Message: {1}", response.Code, response.Message));
            }
        }

        public async Task Disconnect()
        {
            await this.Connection.Send("QUIT\r\n");
            var response = await this.Connection.Receive();
            if (response.Code != 205)
                throw new NntpException(response.Message);
        }
        #endregion

        public async Task<ReadOnlyCollection<string>> GetCapabilities()
        {
            await this.Connection.Send("CAPABILITIES\r\n");
            var response = await this.Connection.ReceiveMultiline();
            if (response.Code != 101)
                throw new NntpException(response.Message);

            return response.Lines.ToList().AsReadOnly();
        }

        public async Task<ReadOnlyCollection<string>> GetNewsgroups()
        {
            await this.Connection.Send("LIST\r\n");
            var response = await this.Connection.ReceiveMultiline();
            if (response.Code != 215)
                throw new NntpException(response.Message);

            var retval = response.Lines.Select(line => line.Split(' ')).Select(values => values[0]).ToList();

            return retval.AsReadOnly();
        }
        public async Task<ReadOnlyCollection<OverResponse>> GetNews(string newsgroup)
        {
            var topics = new List<string>();
            await this.Connection.Send("GROUP {0}\r\n", newsgroup);
            var response = await this.Connection.Receive();
            if (response.Code != 211)
                throw new NntpException(response.Message);

            char[] seps = { ' ' };
            var values = response.Message.Split(seps);

            if (!int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                throw new InvalidOperationException($"Cannot parse {values[0]} to an integer for 'number'");

            if (!int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int low))
                throw new InvalidOperationException($"Cannot parse {values[1]} to an integer for 'low'");

            if (!int.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int high))
                throw new InvalidOperationException($"Cannot parse {values[2]} to an integer for 'high'");

            if (high == low - 1)
            {
                // Empty group
                return new ReadOnlyCollection<OverResponse>(new List<OverResponse>(0));
            }

            return await Over(low, high);
        }

        public async Task<ReadOnlyCollection<string>> Article(int articleNumber)
        {
            CurrentArticleNumber = articleNumber;
            await this.Connection.Send($"ARTICLE {articleNumber}\r\n");
            var response2 = await this.Connection.ReceiveMultiline();
            if (response2.Code == 423) // No article with that number
                return null;

            if (response2.Code == 220)
                return response2.Lines;
            else
                throw new NntpException(response2.Message);
        }

        public async Task<ReadOnlyCollection<OverResponse>> Over(int low, int high)
        {
            await this.Connection.Send($"OVER {low}-{high}\r\n");
            var response2 = await this.Connection.ReceiveMultiline();
            if (response2.Code != 224)
                throw new NntpException(response2.Message);

            var ret = new List<OverResponse>();

            foreach (var line in response2.Lines)
            {
                var parts = line.Split('\t');
                ret.Add(new OverResponse
                {
                    ArticleNumber = int.Parse(parts[0]),
                    Subject = parts[1],
                    From = parts[2],
                    Date = parts[3],
                    MessageID = parts[4],
                    References = parts[5],
                    Bytes = int.Parse(parts[6]),
                    Lines = int.Parse(parts[7]),
                });
            }

            return new ReadOnlyCollection<OverResponse>(ret);
        }

        public async Task Post(string newsgroup, string subject, string from, string content)
        {
            await this.Connection.Send("POST\r\n");
            var response = await this.Connection.Receive();
            if (response.Code != 340)
                throw new NntpException(response.Message);

            await this.Connection.Send("From: {0}\r\nNewsgroups: {1}\r\nSubject: {2}\r\n\r\n{3}\r\n.\r\n", from, newsgroup, subject, content);
            response = await this.Connection.Receive();
            if (response.Code != 240)
                throw new NntpException(response.Message);
        }

        public async Task SetCurrentGroup(string newsgroup)
        {
            await this.Connection.Send("GROUP {0}\r\n", newsgroup);
            var response = await this.Connection.Receive();
            if (response.Code == 411)
                throw new NntpException("No such group: {0}", new[] { newsgroup });
        }
    }
}

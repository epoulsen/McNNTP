﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="Connection.cs" company="Sean McElroy">
//   Copyright Sean McElroy, 2014.  All rights reserved.
// </copyright>
// <summary>
//   A connection from a client to the server
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace McNNTP.Core.Server
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Reflection;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;
    using JetBrains.Annotations;
    using log4net;
    using McNNTP.Common;
    using McNNTP.Data;

    using MoreLinq;
    using NHibernate;
    using NHibernate.Criterion;
    using NHibernate.Linq;
    using NHibernate.Transform;

    /// <summary>
    /// A connection from a client to the server
    /// </summary>
    internal class Connection
    {
        /// <summary>
        /// The size of the stream receive buffer
        /// </summary>
        private const int BufferSize = 1024;

        /// <summary>
        /// A command-indexed dictionary with function pointers to support client command
        /// </summary>
        private static readonly Dictionary<string, Func<Connection, string, Task<CommandProcessingResult>>> CommandDirectory;

        /// <summary>
        /// The logging utility instance to use to log events from this class
        /// </summary>
        private static readonly ILog Logger = LogManager.GetLogger(typeof(Connection));

        /// <summary>
        /// The server instance to which this connection belongs
        /// </summary>
        [NotNull]
        private readonly NntpServer server;

        /// <summary>
        /// The <see cref="TcpClient"/> that accepted this connection.
        /// </summary>
        [NotNull] 
        private readonly TcpClient client;

        /// <summary>
        /// The <see cref="Stream"/> instance retrieved from the <see cref="TcpClient"/> that accepted this connection.
        /// </summary>
        [NotNull] 
        private readonly Stream stream;

        /// <summary>
        /// The stream receive buffer
        /// </summary>
        [NotNull]
        private readonly byte[] buffer = new byte[BufferSize];

        /// <summary>
        /// The received data buffer appended to from the stream buffer
        /// </summary>
        [NotNull]
        private readonly StringBuilder builder = new StringBuilder();

        /// <summary>
        /// The remote IP address to which the connection is established
        /// </summary>
        [NotNull]
        private readonly IPAddress remoteAddress;

        /// <summary>
        /// The remote TCP port number for the remote endpoint to which the connection is established
        /// </summary>
        private readonly int remotePort;

        /// <summary>
        /// The local IP address to which the connection is established
        /// </summary>
        [NotNull]
        private readonly IPAddress localAddress;

        /// <summary>
        /// The local TCP port number for the local endpoint to which the connection is established
        /// </summary>
        private readonly int localPort;

        /// <summary>
        /// For commands that handle conversational request-replies, this is a reference to the
        /// command that should handle new input received by the main process loop.
        /// </summary>
        [CanBeNull]
        private CommandProcessingResult inProcessCommand;

        /// <summary>
        /// Initializes static members of the <see cref="Connection"/> class.
        /// </summary>
        static Connection()
        {
            CommandDirectory = new Dictionary<string, Func<Connection, string, Task<CommandProcessingResult>>>
                {
                    { "ARTICLE", async (c, data) => await c.Article(data) },
                    { "AUTHINFO", async (c, data) => await c.AuthInfo(data) },
                    { "BODY", async (c, data) => await c.Body(data) },
                    { "CAPABILITIES", async (c, data) => await c.Capabilities() },
                    { "DATE", async (c, data) => await c.Date() },
                    { "GROUP", async (c, data) => await c.Group(data) },
                    { "HDR", async (c, data) => await c.Hdr(data) },
                    { "HEAD", async (c, data) => await c.Head(data) },
                    { "HELP", async (c, data) => await c.Help() },
                    { "LAST", async (c, data) => await c.Last() },
                    { "LIST", async (c, data) => await c.List(data) },
                    { "LISTGROUP", async (c, data) => await c.ListGroup(data) },
                    { "MODE", async (c, data) => await c.Mode(data) },
                    { "NEWGROUPS", async (c, data) => await c.NewGroups(data) },
                    { "NEWNEWS", async (c, data) => await c.NewNews(data) },
                    { "NEXT", async (c, data) => await c.Next() },
                    { "OVER", async (c, data) => await c.Over(data) },
                    { "POST", async (c, data) => await c.Post() },
                    { "STAT", async (c, data) => await c.Stat(data) },
                    { "XFEATURE", async (c, data) => await c.XFeature(data) },
                    { "XHDR", async (c, data) => await c.XHDR(data) },
                    { "XOVER", async (c, data) => await c.XOver(data) },
                    { "QUIT", async (c, data) => await c.Quit() }
                };
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Connection"/> class.
        /// </summary>
        /// <param name="server">The server instance that owns this connection</param>
        /// <param name="client">The <see cref="TcpClient"/> that accepted this connection</param>
        /// <param name="stream">The <see cref="Stream"/> from the <paramref name="client"/></param>
        /// <param name="tls">Whether or not the connection has implicit Transport Layer Security</param>
        public Connection(
            [NotNull] NntpServer server,
            [NotNull] TcpClient client,
            [NotNull] Stream stream,
            bool tls = false)
        {
            AllowStartTls = server.AllowStartTLS;
            CanPost = server.AllowPosting;
            this.client = client;
            this.client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            PathHost = server.PathHost;
            ShowBytes = server.ShowBytes;
            ShowCommands = server.ShowCommands;
            ShowData = server.ShowData;
            this.server = server;
            this.stream = stream;
            TLS = tls;

            var remoteIpEndpoint = (IPEndPoint)this.client.Client.RemoteEndPoint;
            this.remoteAddress = remoteIpEndpoint.Address;
            this.remotePort = remoteIpEndpoint.Port;
            var localIpEndpoint = (IPEndPoint)this.client.Client.LocalEndPoint;
            this.localAddress = localIpEndpoint.Address;
            this.localPort = localIpEndpoint.Port;
        }

        /// <summary>
        /// Gets or sets a value indicating whether the connection can be upgraded to use
        /// Transport Later Security (TLS) through the STARTTLS command.
        /// </summary>
        [PublicAPI]
        public bool AllowStartTls { get; set; }

        public bool CanPost { get; private set; }

        public bool ShowBytes { get; set; }

        public bool ShowCommands { get; set; }

        public bool ShowData { get; set; }

        public string PathHost { get; set; }

        #region Authentication
        [CanBeNull]
        public string Username { get; set; }

        [CanBeNull]
        public Administrator Identity { get; set; }

        public bool TLS { get; set; }
        #endregion

        #region Compression
        /// <summary>
        /// Gets a value indicating whether the connection should have compression enabled
        /// </summary>
        [PublicAPI]
        public bool Compression { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the connection is compressed with the UNIX GZip protocol
        /// </summary>
        [PublicAPI]
        public bool CompressionGZip { get; private set; }

        /// <summary>
        /// Gets a value indicating whether message terminators are also compressed
        /// </summary>
        [PublicAPI]
        public bool CompressionTerminator { get; private set; }
        #endregion

        /// <summary>
        /// Gets the newsgroup currently selected by this connection
        /// </summary>
        [PublicAPI, CanBeNull]
        public string CurrentNewsgroup { get; private set; }

        /// <summary>
        /// Gets the article number currently selected by this connection for the selected newsgroup
        /// </summary>
        [PublicAPI]
        public long? CurrentArticleNumber { get; private set; }

        #region Derived instance properties
        /// <summary>
        /// Gets the remote IP address to which the connection is established
        /// </summary>
        [NotNull]
        public IPAddress RemoteAddress
        {
            get { return this.remoteAddress; }
        }

        /// <summary>
        /// Gets the remote TCP port number for the remote endpoint to which the connection is established
        /// </summary>
        public int RemotePort
        {
            get { return this.remotePort; }
        }

        /// <summary>
        /// Gets the local IP address to which the connection is established
        /// </summary>
        [NotNull]
        public IPAddress LocalAddress
        {
            get { return this.localAddress; }
        }

        /// <summary>
        /// Gets the local TCP port number for the local endpoint to which the connection is established
        /// </summary>
        public int LocalPort
        {
            get { return this.localPort; }
        }
        #endregion

        #region IO and Connection Management
        public async void Process()
        {
            // ReSharper disable ConvertIfStatementToConditionalTernaryExpression
            if (CanPost)
                // ReSharper restore ConvertIfStatementToConditionalTernaryExpression
                await Send("200 Service available, posting allowed\r\n");
            else
                await Send("201 Service available, posting prohibited\r\n");

            Debug.Assert(this.stream != null, "The stream was 'null', but it should not have been because the connection was accepted and processing is beginning.");

            bool send403;

            try
            {
                while (true)
                {
                    if (!this.client.Connected || !this.client.Client.Connected) return;

                    if (!this.stream.CanRead)
                    {
                        await Shutdown();
                        return;
                    }

                    var bytesRead = await stream.ReadAsync(this.buffer, 0, BufferSize);

                    // There  might be more data, so store the data received so far.
                    this.builder.Append(Encoding.ASCII.GetString(this.buffer, 0, bytesRead));

                    // Not all data received OR no more but not yet ending with the delimiter. Get more.
                    var content = this.builder.ToString();
                    if (bytesRead == BufferSize || !content.EndsWith("\r\n", StringComparison.Ordinal))
                    {
                        // Read some more.
                        continue;
                    }

                    // All the data has been read from the 
                    // client. Display it on the console.
                    if (ShowBytes && ShowData)
                        Logger.TraceFormat(
                            "{0}:{1} >{2}> {3} bytes: {4}",
                            RemoteAddress,
                            RemotePort,
                            TLS ? "!" : ">",
                            content.Length,
                            content.TrimEnd('\r', '\n'));
                    else if (ShowBytes)
                        Logger.TraceFormat(
                            "{0}:{1} >{2}> {3} bytes",
                            RemoteAddress,
                            RemotePort,
                            TLS ? "!" : ">",
                            content.Length);
                    else if (ShowData)
                        Logger.TraceFormat(
                            "{0}:{1} >{2}> {3}",
                            RemoteAddress,
                            RemotePort,
                            TLS ? "!" : ">",
                            content.TrimEnd('\r', '\n'));

                    if (this.inProcessCommand != null && this.inProcessCommand.MessageHandler != null)
                    {
                        // Ongoing read - don't parse it for commands
                        this.inProcessCommand = await inProcessCommand.MessageHandler(content, inProcessCommand);
                        if (inProcessCommand != null && inProcessCommand.IsQuitting)
                            inProcessCommand = null;
                    }
                    else
                    {
                        var command = content.Split(' ').First().TrimEnd('\r', '\n').ToUpperInvariant();
                        if (CommandDirectory.ContainsKey(command))
                        {
                            try
                            {
                                if (ShowCommands)
                                    Logger.TraceFormat(
                                        "{0}:{1} >{2}> {3}",
                                        RemoteAddress,
                                        RemotePort,
                                        TLS ? "!" : ">",
                                        content.TrimEnd('\r', '\n'));

                                var result = await CommandDirectory[command].Invoke(this, content);

                                if (!result.IsHandled) await Send("500 Unknown command\r\n");
                                else if (result.MessageHandler != null) this.inProcessCommand = result;
                                else if (result.IsQuitting) return;
                            }
                            catch (Exception ex)
                            {
                                send403 = true;
                                Logger.Error("Exception processing a command", ex);
                                break;
                            }
                        }
                        else await Send("500 Unknown command\r\n");
                    }

                    this.builder.Clear();
                }
            }
            catch (DecoderFallbackException dfe)
            {
                send403 = true;
                Logger.Error("Decoder Fallback Exception socket " + RemoteAddress, dfe);
            }
            catch (IOException se)
            {
                send403 = true;
                Logger.Error("I/O Exception on socket " + RemoteAddress, se);
            }
            catch (SocketException se)
            {
                send403 = true;
                Logger.Error("Socket Exception on socket " + RemoteAddress, se);
            }
            catch (NotSupportedException nse)
            {
                Logger.Error("Not Supported Exception", nse);
                return;
            }
            catch (ObjectDisposedException ode)
            {
                Logger.Error("Object Disposed Exception", ode);
                return;
            }

            if (send403)
                await Send("403 Archive server temporarily offline\r\n");
        }

        /// <summary>
        /// Sends the formatted data to the client
        /// </summary>
        /// <param name="format">The data, or format string for data, to send to the client</param>
        /// <param name="args">The argument applied as a format string to <paramref name="format"/> to create the data to send to the client</param>
        /// <returns>A value indicating whether or not the transmission was successful</returns>
        [StringFormatMethod("format"), NotNull]
        private async Task<bool> Send([NotNull] string format, [NotNull] params object[] args)
        {
            return await SendInternal(string.Format(CultureInfo.InvariantCulture, format, args), false);
        }

        /// <summary>
        /// Sends the formatted data to the client
        /// </summary>
        /// <param name="format">The data, or format string for data, to send to the client</param>
        /// <param name="args">The argument applied as a format string to <paramref name="format"/> to create the data to send to the client</param>
        /// <returns>A value indicating whether or not the transmission was successful</returns>
        [StringFormatMethod("format"), NotNull]
        private async Task<bool> SendCompressed([NotNull] string format, [NotNull] params object[] args)
        {
            return await SendInternal(string.Format(CultureInfo.InvariantCulture, format, args), true);
        }

        private async Task<bool> SendInternal([NotNull] string data, bool compressedIfPossible)
        {
            // Convert the string data to byte data using ASCII encoding.
            byte[] byteData;
            if (compressedIfPossible && Compression && CompressionGZip && CompressionTerminator)
                byteData = await data.GZipCompress();
            else
                byteData = Encoding.UTF8.GetBytes(data);

            try
            {
                // Begin sending the data to the remote device.
                await this.stream.WriteAsync(byteData, 0, byteData.Length);
                if (ShowBytes && ShowData)
                    Logger.TraceFormat(
                        "{0}:{1} <{2}{3} {4} bytes: {5}",
                        RemoteAddress,
                        RemotePort,
                        TLS ? "!" : "<",
                        compressedIfPossible && CompressionGZip ? "G" : "<",
                        byteData.Length,
                        data.TrimEnd('\r', '\n'));
                else if (ShowBytes)
                    Logger.TraceFormat(
                        "{0}:{1} <{2}{3} {4} bytes",
                        RemoteAddress,
                        RemotePort,
                        TLS ? "!" : "<",
                        compressedIfPossible && CompressionGZip ? "G" : "<",
                        byteData.Length);
                else if (ShowData)
                    Logger.TraceFormat(
                        "{0}:{1} <{2}{3} {4}",
                        RemoteAddress,
                        RemotePort,
                        TLS ? "!" : "<",
                        compressedIfPossible && CompressionGZip ? "G" : "<",
                        data.TrimEnd('\r', '\n'));

                return true;
            }
            catch (IOException)
            {
                // Don't send 403 - the sending socket isn't working.
                Logger.VerboseFormat("{0}:{1} XXX CONNECTION TERMINATED", RemoteAddress, RemotePort);
                return false;
            }
            catch (SocketException)
            {
                // Don't send 403 - the sending socket isn't working.
                Logger.VerboseFormat("{0}:{1} XXX CONNECTION TERMINATED", RemoteAddress, RemotePort);
                return false;
            }
            catch (ObjectDisposedException)
            {
                Logger.VerboseFormat("{0}:{1} XXX CONNECTION TERMINATED", RemoteAddress, RemotePort);
                return false;
            }
        }

        public async Task Shutdown()
        {
            if (this.client.Connected)
            {
                await Send("205 closing connection\r\n");
                this.client.Client.Shutdown(SocketShutdown.Both);
                this.client.Close();
            }

            this.server.RemoveConnection(this);
        }
        #endregion

        #region Commands
        private async Task<CommandProcessingResult> Article(string content)
        {
            var param = 
                (string.Compare(content, "ARTICLE\r\n", StringComparison.OrdinalIgnoreCase) == 0) ||
                (string.Compare(content, "ARTICLE \r\n", StringComparison.OrdinalIgnoreCase) == 0)
                ? null
                : content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (string.IsNullOrEmpty(param))
            {
                if (!CurrentArticleNumber.HasValue)
                {
                    await Send("430 No article with that message-id\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else if (string.IsNullOrEmpty(CurrentNewsgroup) && !param.StartsWith("<", StringComparison.Ordinal))
            {
                await Send("412 No newsgroup selected\r\n");
                return new CommandProcessingResult(true);
            }

            using (var session = Database.SessionUtility.OpenSession())
            {
                ArticleNewsgroup articleNewsgroup;
                int type;
                if (string.IsNullOrEmpty(param))
                {
                    if (CurrentNewsgroup == null)
                    {
                        await Send("412 No newsgroup selected\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (CurrentNewsgroup.EndsWith(".deleted"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber);
                    else if (CurrentNewsgroup.EndsWith(".pending"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber);
                    else
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == CurrentArticleNumber);
                    type = 3;
                }
                else if (param.StartsWith("<", StringComparison.Ordinal))
                {
                    articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Article.MessageId == param);
                    type = 1;
                }
                else
                {
                    int articleNumber;
                    if (!int.TryParse(param, out articleNumber))
                    {
                        await Send("423 No article with that number\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (CurrentNewsgroup == null)
                    {
                        await Send("412 No newsgroup selected\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (CurrentNewsgroup.EndsWith(".deleted"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == articleNumber);
                    else if (CurrentNewsgroup.EndsWith(".pending"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == articleNumber);
                    else
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == articleNumber);
                    type = 2;
                }

                session.Close();

                if (articleNewsgroup == null)
                    switch (type)
                    {
                        case 1:
                            await Send("430 No article with that message-id\r\n");
                            break;
                        case 2:
                            await Send("423 No article with that number\r\n");
                            break;
                        case 3:
                            await Send("420 Current article number is invalid\r\n");
                            break;

                    }
                else
                {
                    switch (type)
                    {
                        case 1:
                            await Send(
                                "220 {0} {1} Article follows (multi-line)\r\n",
                                (!string.IsNullOrEmpty(CurrentNewsgroup) && string.CompareOrdinal(articleNewsgroup.Newsgroup.Name, CurrentNewsgroup) == 0) ? articleNewsgroup.Number : 0,
                                articleNewsgroup.Article.MessageId);
                            break;
                        case 2:
                            await Send("220 {0} {1} Article follows (multi-line)\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId);
                            break;
                        case 3:
                            await Send("220 {0} {1} Article follows (multi-line)\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId);
                            break;
                    }

                    await Send(articleNewsgroup.Article.Headers + "\r\n\r\n");
                    await Send(articleNewsgroup.Article.Body + "\r\n.\r\n");
                }
            }

            return new CommandProcessingResult(true);
        }

        private async Task<CommandProcessingResult> AuthInfo(string content)
        {
            // RFC 4643 - NNTP AUTHENTICATION
            var param = (string.Compare(content, "AUTHINFO\r\n", StringComparison.OrdinalIgnoreCase) == 0)
                ? null
                : content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (string.IsNullOrWhiteSpace(param))
            {
                await Send("481 Authentication failed/rejected\r\n");
                return new CommandProcessingResult(true);
            }

            var args = param.Split(' ');

            if (string.Compare(args[0], "USER", StringComparison.OrdinalIgnoreCase) == 0)
            {
                Username = args.Skip(1).Aggregate((c, n) => c + " " + n);
                await Send("381 Password required\r\n");
                return new CommandProcessingResult(true);
            }

            if (string.Compare(args[0], "PASS", StringComparison.OrdinalIgnoreCase) == 0)
            {
                if (Username == null)
                {
                    await Send("482 Authentication commands issued out of sequence\r\n");
                    return new CommandProcessingResult(true);
                }

                if (Identity != null)
                {
                    await Send("502 Command unavailable\r\n");
                    return new CommandProcessingResult(true);
                }

                var password = args.Skip(1).Aggregate((c, n) => c + " " + n);
                var saltBytes = new byte[64];
                var rng = RandomNumberGenerator.Create();
                rng.GetNonZeroBytes(saltBytes);

                Administrator admin;
                using (var session = Database.SessionUtility.OpenSession())
                {
                    admin = session.Query<Administrator>().Fetch(a => a.Moderates).SingleOrDefault(a => a.Username == Username);
                    session.Close();
                }

                if (admin == null)
                {
                    if (this.server.LdapDirectoryConfiguration != null && this.server.LdapDirectoryConfiguration.AutoEnroll)
                    {
                        var memberships =
                            LdapUtility.GetUserGroupMemberships(
                                server.LdapDirectoryConfiguration.LdapServer,
                                server.LdapDirectoryConfiguration.LookupAccountUsername,
                                server.LdapDirectoryConfiguration.LookupAccountPassword,
                                Username);

                        // Auto enroll the user as an administrator.
                        throw new NotImplementedException("Auto enrollment is not yet implemented.");

                        // if (memberships.Any(m => string.Compare(m, this.server.LdapDirectoryConfiguration.AutoEnrollAdminGroup, StringComparison.OrdinalIgnoreCase) == 0))
                        // {
                        //     // Auto enroll the user as an administrator.
                        //     throw new NotImplementedException("Auto enrollment is not yet implemented.");
                        // }
                        // else 
                        // {
                        //     // Auto enroll the user as a non-administrator.
                        //     throw new NotImplementedException("Auto enrollment is not yet implemented.");
                        // }
                    }
                    else
                    {
                        // No user with this username in the local database
                        await Send("481 Authentication failed/rejected\r\n");
                        return new CommandProcessingResult(true);
                    }
                }

                if (this.server.LdapDirectoryConfiguration != null)
                {
                    // LDAP authentication
                    if (!LdapUtility.AuthenticateUser(this.server.LdapDirectoryConfiguration.LdapServer,
                            this.server.LdapDirectoryConfiguration.SearchPath,
                            Username, password))
                    {
                        Logger.WarnFormat("User {0} failed authentication against LDAP server.", Username);
                        await Send("481 Authentication failed/rejected\r\n");
                        return new CommandProcessingResult(true);
                    }
                }
                else
                {
                    // Local authentication
                    if (admin.PasswordHash != Convert.ToBase64String(new SHA512CryptoServiceProvider().ComputeHash(Encoding.UTF8.GetBytes(string.Concat(admin.PasswordSalt, password)))))
                    {
                        Logger.WarnFormat("User {0} failed authentication against local authentication database.", Username);
                        await Send("481 Authentication failed/rejected\r\n");
                        return new CommandProcessingResult(true);
                    }
                }


                if (admin.LocalAuthenticationOnly &&
                    !IPAddress.IsLoopback(RemoteAddress))
                {
                    await Send("481 Authentication not allowed except locally\r\n");
                    return new CommandProcessingResult(true);
                }

                Identity = admin;
                Logger.InfoFormat("User {0} authenticated from {1}", admin.Username, RemoteAddress);

                await Send("281 Authentication accepted\r\n");
                return new CommandProcessingResult(true);
            }

            //if (string.Compare(args[0], "GENERIC", StringComparison.OrdinalIgnoreCase) == 0)
            //{
            //    Send("501 Command not supported\r\n");
            //    return new CommandProcessingResult(true);
            //}

            await Send("501 Command not supported\r\n");
            return new CommandProcessingResult(true);
        }

        private async Task<CommandProcessingResult> Body(string content)
        {
            var param = (string.Compare(content, "BODY\r\n", StringComparison.OrdinalIgnoreCase) == 0)
                ? null
                : content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (string.IsNullOrEmpty(param))
            {
                if (!CurrentArticleNumber.HasValue)
                {
                    await Send("430 No article with that message-id\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else
            {
                if (CurrentNewsgroup == null)
                {
                    await Send("412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }
            }

            using (var session = Database.SessionUtility.OpenSession())
            {
                int type;
                ArticleNewsgroup articleNewsgroup;
                if (string.IsNullOrEmpty(param))
                {
                    if (CurrentNewsgroup == null)
                    {
                        await Send("412 No newsgroup selected\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (CurrentNewsgroup.EndsWith(".deleted"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber);
                    else if (CurrentNewsgroup.EndsWith(".pending"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber);
                    else
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == CurrentArticleNumber);
                    type = 3;
                }
                else if (param.StartsWith("<", StringComparison.Ordinal))
                {
                    articleNewsgroup = session.Query<ArticleNewsgroup>().FirstOrDefault(an => !an.Cancelled && !an.Pending && an.Article.MessageId == param);
                    type = 1;
                }
                else
                {
                    if (CurrentNewsgroup == null)
                    {
                        await Send("412 No newsgroup selected\r\n");
                        return new CommandProcessingResult(true);
                    }

                    int articleNumber;
                    if (!int.TryParse(param, out articleNumber))
                    {
                        await Send("423 No article with that number\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (CurrentNewsgroup.EndsWith(".deleted"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == articleNumber);
                    else if (CurrentNewsgroup.EndsWith(".pending"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == articleNumber);
                    else
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == articleNumber);
                    type = 2;
                }

                session.Close();

                if (articleNewsgroup == null)
                    switch (type)
                    {
                        case 1:
                            await Send("430 No article with that message-id\r\n");
                            break;
                        case 2:
                            await Send("423 No article with that number\r\n");
                            break;
                        case 3:
                            await Send("420 Current article number is invalid\r\n");
                            break;
                    }
                else
                {
                    switch (type)
                    {
                        case 1:
                            await Send(
                                "222 {0} {1} Body follows (multi-line)\r\n",
                                (!string.IsNullOrEmpty(CurrentNewsgroup) && string.CompareOrdinal(articleNewsgroup.Newsgroup.Name, CurrentNewsgroup) == 0) ? articleNewsgroup.Number : 0,
                                articleNewsgroup.Article.MessageId);
                            break;
                        case 2:
                            await Send("222 {0} {1} Body follows (multi-line)\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId);
                            break;
                        case 3:
                            await Send("222 {0} {1} Body follows (multi-line)\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId);
                            break;
                    }

                    await Send(articleNewsgroup.Article.Body + "\r\n.\r\n");
                }
            }

            return new CommandProcessingResult(true);
        }

        /// <summary>
        /// Handles the CAPABILITIES command from a client, which allows a client to retrieve a list
        /// of the functionality available in this server. 
        /// </summary>
        /// <returns>A command processing result specifying the command is handled.</returns>
        /// <remarks>See <a href="http://tools.ietf.org/html/rfc3977#section-5.2">RFC 3977</a> for more information.</remarks>
        private async Task<CommandProcessingResult> Capabilities()
        {
            var sb = new StringBuilder();
            sb.Append("101 Capability list:\r\n");
            sb.Append("VERSION 2\r\n");

            // sb.Append("IHAVE\r\n");
            sb.Append("HDR\r\n");
            sb.Append("LIST ACTIVE NEWSGROUPS ACTIVE.TIMES DISTRIB.PATS HEADERS OVERVIEW.FMT\r\n");
            sb.Append("MODE-READER\r\n");
            sb.Append("NEWNEWS\r\n");
            sb.Append("OVER MSGID\r\n");
            sb.Append("POST\r\n");
            sb.Append("READER\r\n");
            if (AllowStartTls)
                sb.Append("STARTTLS\r\n");
            sb.Append("XFEATURE-COMPRESS GZIP TERMINATOR\r\n");
            sb.Append("IMPLEMENTATION McNNTP 1.0.0\r\n");
            sb.Append(".\r\n");
            await Send(sb.ToString());
            return new CommandProcessingResult(true);
        }

        /// <summary>
        /// Handles the DATE command from a client, which allows a client to retrieve the current
        /// time from the server's perspective
        /// </summary>
        /// <returns>A command processing result specifying the command is handled.</returns>
        /// <remarks>See <a href="http://tools.ietf.org/html/rfc3977#section-7.1">RFC 3977</a> for more information.</remarks>
        private async Task<CommandProcessingResult> Date()
        {
            await Send("111 {0:yyyyMMddHHmmss}\r\n", DateTime.UtcNow);
            return new CommandProcessingResult(true);
        }

        /// <summary>
        /// Handles the GROUP command from a client, which allows a client to set the currently
        /// selected newsgroup.
        /// </summary>
        /// <param name="content">The full command request provided by the client</param>
        /// <returns>A command processing result specifying the command is handled.</returns>
        /// <remarks>See <a href="http://tools.ietf.org/html/rfc3977#section-6.1.1">RFC 3977</a> for more information.</remarks>
        private async Task<CommandProcessingResult> Group(string content)
        {
            content = content.TrimEnd('\r', '\n').Substring(content.IndexOf(' ') + 1).Split(' ')[0];
            Newsgroup ng;
            using (var session = Database.SessionUtility.OpenSession())
            {
                ng = session.Query<Newsgroup>().AddMetagroups(session, Identity).SingleOrDefault(n => n.Name == content);
                session.Close();
            }

            if (ng == null)
                await Send("411 {0} is unknown\r\n", content);
            else
            {
                CurrentNewsgroup = ng.Name;
                CurrentArticleNumber = ng.LowWatermark;

                // ReSharper disable ConvertIfStatementToConditionalTernaryExpression
                if (ng.PostCount == 0)
                    // ReSharper restore ConvertIfStatementToConditionalTernaryExpression
                    await Send("211 0 0 0 {0}\r\n", ng.Name);
                else
                    await Send("211 {0} {1} {2} {3}\r\n", ng.PostCount, ng.LowWatermark, ng.HighWatermark, ng.Name);
            }

            return new CommandProcessingResult(true);
        }

        private async Task<CommandProcessingResult> Hdr(string content)
        {
            var parts = content.TrimEnd('\r', '\n').Split(' ');
            if (parts.Length < 2 || parts.Length > 3)
            {
                await Send("501 Syntax Error\r\n");
                return new CommandProcessingResult(true);
            }

            int type;

            if (parts.Length == 3 && parts[2].StartsWith("<", StringComparison.Ordinal))
                type = 1;
            else if (parts.Length == 3 && !parts[2].StartsWith("<", StringComparison.Ordinal))
            {
                type = 2;
                int articleId;
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out articleId))
                {
                    await Send("501 Syntax Error\r\n");
                    return new CommandProcessingResult(true);
                }

                if (string.IsNullOrEmpty(CurrentNewsgroup))
                {
                    await Send("412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else
            {
                Debug.Assert(parts.Length == 2, string.Format("Two parts of the command text '{0}' were expected, but fewer or more parts were found.", content.TrimEnd('\r', '\n')));
                type = 3;
                if (string.IsNullOrEmpty(CurrentNewsgroup))
                {
                    await Send("412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }

                if (!CurrentArticleNumber.HasValue)
                {
                    await Send("420 Current article number is invalid\r\n");
                    return new CommandProcessingResult(true);
                }
            }

            using (var session = Database.SessionUtility.OpenSession())
            {
                IEnumerable<ArticleNewsgroup> articleNewsgroups;
                switch (type)
                {
                    case 1:
                        articleNewsgroups = new[] { session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Article.MessageId == parts[2]) };
                        break;
                    case 2:
                        var range = ParseRange(parts[2]);
                        if (range == null || range.Equals(default(System.Tuple<int, int?>)))
                        {
                            await Send("501 Syntax Error\r\n");
                            return new CommandProcessingResult(true);
                        }

                        Debug.Assert(CurrentNewsgroup != null);
                        if (CurrentNewsgroup.EndsWith(".deleted"))
                            articleNewsgroups = range.Item2.HasValue
                                ? session.Query<ArticleNewsgroup>().Where(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number >= range.Item1 && an.Number <= range.Item2)
                                : session.Query<ArticleNewsgroup>().Where(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number >= range.Item1);
                        else if (CurrentNewsgroup.EndsWith(".pending"))
                            articleNewsgroups = (range.Item2.HasValue)
                                ? session.Query<ArticleNewsgroup>().Where(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number >= range.Item1 && an.Number <= range.Item2)
                                : session.Query<ArticleNewsgroup>().Where(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number >= range.Item1);
                        else
                            articleNewsgroups = (range.Item2.HasValue)
                                ? session.Query<ArticleNewsgroup>().Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number >= range.Item1 && an.Number <= range.Item2)
                                : session.Query<ArticleNewsgroup>().Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number >= range.Item1);
                        break;
                    case 3:
                        Debug.Assert(CurrentArticleNumber.HasValue);

                        Debug.Assert(CurrentNewsgroup != null);
                        if (CurrentNewsgroup.EndsWith(".deleted"))
                            articleNewsgroups = session.Query<ArticleNewsgroup>().Where(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber.Value);
                        else if (CurrentNewsgroup.EndsWith(".pending"))
                            articleNewsgroups = session.Query<ArticleNewsgroup>().Where(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber.Value);
                        else
                            articleNewsgroups = session.Query<ArticleNewsgroup>().Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == CurrentArticleNumber.Value);
                        break;
                    default:
                        // Unrecognized...
                        await Send("501 Syntax Error\r\n");
                        return new CommandProcessingResult(true);
                }

                session.Close();

                if (!articleNewsgroups.Any())
                    switch (type)
                    {
                        case 1:
                            await Send("430 No article with that message-id\r\n");
                            break;
                        case 2:
                            await Send("423 No articles in that range\r\n");
                            break;
                        case 3:
                            await Send("420 Current article number is invalid\r\n");
                            break;
                    }
                else
                {
                    await Send("225 Headers follow (multi-line)\r\n");

                    Func<Article, string> headerFunction;
                    switch (parts[1].ToUpperInvariant())
                    {
                        case "DATE":
                            headerFunction = a => a.Date;
                            break;
                        case "FROM":
                            headerFunction = a => a.From;
                            break;
                        case "MESSAGE-ID":
                            headerFunction = a => a.MessageId;
                            break;
                        case "REFERENCES":
                            headerFunction = a => a.References;
                            break;
                        case "SUBJECT":
                            headerFunction = a => a.Subject;
                            break;
                        default:
                            {
                                Dictionary<string, string> headers, headersAndFullLines;
                                headerFunction = a => Data.Article.TryParseHeaders(a.Headers, out headers, out headersAndFullLines)
                                    ? a.GetHeader(parts[1])
                                    : null;
                                break;
                            }
                    }

                    foreach (var articleNewsgroup in articleNewsgroups)
                        if (type == 1)
                            await Send(
                                "{0} {1}\r\n",
                                (!string.IsNullOrEmpty(CurrentNewsgroup) && string.CompareOrdinal(articleNewsgroup.Newsgroup.Name, CurrentNewsgroup) == 0) ? articleNewsgroup.Article.MessageId : "0",
                                headerFunction.Invoke(articleNewsgroup.Article));
                        else
                            await Send(
                                "{0} {1}\r\n",
                                articleNewsgroup.Number,
                                headerFunction.Invoke(articleNewsgroup.Article));

                    await Send(".\r\n");
                }
            }

            return new CommandProcessingResult(true);
        }

        private async Task<CommandProcessingResult> Head(string content)
        {
            var param = (string.Compare(content, "HEAD\r\n", StringComparison.OrdinalIgnoreCase) == 0)
                ? null
                : content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (string.IsNullOrEmpty(param))
            {
                if (!CurrentArticleNumber.HasValue)
                {
                    await Send("430 No article with that message-id\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(CurrentNewsgroup))
                {
                    await Send("412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }
            }

            using (var session = Database.SessionUtility.OpenSession())
            {
                ArticleNewsgroup articleNewsgroup;
                int type;
                if (string.IsNullOrEmpty(param))
                {
                    if (CurrentNewsgroup == null)
                    {
                        await Send("412 No newsgroup selected\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (CurrentNewsgroup.EndsWith(".deleted"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber);
                    else if (CurrentNewsgroup.EndsWith(".pending"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber);
                    else
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == CurrentArticleNumber);
                    type = 3;
                }
                else if (param.StartsWith("<", StringComparison.Ordinal))
                {
                    articleNewsgroup = session.Query<ArticleNewsgroup>().FirstOrDefault(an => an.Article.MessageId == param);
                    type = 1;
                }
                else
                {
                    if (CurrentNewsgroup == null)
                    {
                        await Send("412 No newsgroup selected\r\n");
                        return new CommandProcessingResult(true);
                    }
                    
                    int articleNumber;
                    if (!int.TryParse(param, out articleNumber))
                    {
                        await Send("423 No article with that number\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (CurrentNewsgroup.EndsWith(".deleted"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == articleNumber);
                    else if (CurrentNewsgroup.EndsWith(".pending"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == articleNumber);
                    else
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == articleNumber);
                    type = 2;
                }

                session.Close();

                if (articleNewsgroup == null)
                {
                    switch (type)
                    {
                        case 1:
                            await Send("430 No article with that message-id\r\n");
                            break;
                        case 2:
                            await Send("423 No article with that number\r\n");
                            break;
                        case 3:
                            await Send("420 Current article number is invalid\r\n");
                            break;
                    }
                }
                else
                {
                    switch (type)
                    {
                        case 1:
                            await Send("221 {0} {1} Headers follow (multi-line)\r\n", string.CompareOrdinal(articleNewsgroup.Newsgroup.Name, CurrentNewsgroup) == 0 ? articleNewsgroup.Number : 0, articleNewsgroup.Article.MessageId);
                            break;
                        case 2:
                            await Send("221 {0} {1} Headers follow (multi-line)\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId);
                            break;
                        case 3:
                            await Send("221 {0} {1} Headers follow (multi-line)\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId);
                            break;
                    }

                    await Send(articleNewsgroup.Article.Headers + "\r\n.\r\n");
                }
            }

            return new CommandProcessingResult(true);
        }

        /// <summary>
        /// Handles the HELP command from a client, which allows a client to retrieve
        /// help text from a help file.
        /// </summary>
        /// <returns>A command processing result specifying the command is handled.</returns>
        /// <remarks>See <a href="http://tools.ietf.org/html/rfc3977#section-7.2">RFC 3977</a> for more information.</remarks>
        [NotNull, Pure]
        private async Task<CommandProcessingResult> Help()
        {
            var sb = new StringBuilder();
            sb.Append("100 Help text follows\r\n");

            var dirName = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (dirName != null && File.Exists(Path.Combine(dirName, "HelpFile.txt")))
            {
                using (var sr = new StreamReader(Path.Combine(dirName, "HelpFile.txt"), Encoding.UTF8))
                {
                    sb.Append(await sr.ReadToEndAsync());
                    sr.Close();
                }
            }
            else
            {
                sb.Append("The list of commands understood by this server are:\r\n");
                foreach (var cmd in CommandDirectory)
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0}\r\n", cmd.Key);
            }

            if (!sb.ToString().EndsWith("\r\n.\r\n"))
                sb.Append("\r\n.\r\n");

            await Send(sb.ToString());
            return new CommandProcessingResult(true);
        }

        /// <summary>
        /// Handles the LAST command from a client, which allows a client to move the
        /// current article number to the previous article the user can access.
        /// </summary>
        /// <returns>A command processing result specifying the command is handled.</returns>
        /// <remarks>See <a href="http://tools.ietf.org/html/rfc3977#section-6.1.3">RFC 3977</a> for more information.</remarks>
        [NotNull]
        private async Task<CommandProcessingResult> Last()
        {
            // If the currently selected newsgroup is invalid, a 412 response MUST be returned.
            if (string.IsNullOrWhiteSpace(CurrentNewsgroup))
            {
                await Send("412 No newsgroup selected\r\n");
                return new CommandProcessingResult(true);
            }

            var currentArticleNumber = CurrentArticleNumber;

            ArticleNewsgroup previousArticleNewsgroup;

            if (!currentArticleNumber.HasValue)
            {
                await Send("420 Current article number is invalid\r\n");
                return new CommandProcessingResult(true);
            }

            using (var session = Database.SessionUtility.OpenSession())
            {
                previousArticleNewsgroup = session.Query<ArticleNewsgroup>()
                    .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number < currentArticleNumber.Value)
                    .MaxBy(an => an.Number);
                session.Close();
            }

            // If the current article number is already the first article of the newsgroup, a 422 response MUST be returned.
            if (previousArticleNewsgroup == null)
            {
                await Send("422 No previous article in this group\r\n");
                return new CommandProcessingResult(true);
            }

            CurrentArticleNumber = previousArticleNewsgroup.Number;

            await Send("223 {0} {1} retrieved\r\n", previousArticleNewsgroup.Number, previousArticleNewsgroup.Article.MessageId);
            return new CommandProcessingResult(true);
        }

        /// <summary>
        /// Handles the LIST command from a client, which allows a client to retrieve blocks
        /// of information depending on the parameters and arguments supplied with the command.
        /// </summary>
        /// <param name="content">The full command request provided by the client</param>
        /// <returns>A command processing result specifying the command is handled.</returns>
        /// <remarks>See <a href="http://tools.ietf.org/html/rfc3977#section-7.6.1">RFC 3977</a> for more information.</remarks>
        private async Task<CommandProcessingResult> List(string content)
        {
            var contentParts = content.Split(' ');

            if (string.Compare(content, "LIST ACTIVE.TIMES\r\n", StringComparison.OrdinalIgnoreCase) == 0 ||
                content.StartsWith("LIST ACTIVE.TIMES ", StringComparison.OrdinalIgnoreCase))
            {
                IList<Newsgroup> newsGroups = null;

                var wildmat = contentParts.Length == 2
                    ? null
                    : content.TrimEnd('\r', '\n').Split(' ').Skip(2).Aggregate((c, n) => c + " " + n);

                var send403 = false;

                try
                {
                    using (var session = Database.SessionUtility.OpenSession())
                    {
                        newsGroups = wildmat == null
                            ? session.Query<Newsgroup>().AddMetagroups(session, Identity).OrderBy(n => n.Name).ToList()
                            : session.Query<Newsgroup>().AddMetagroups(session, Identity).OrderBy(n => n.Name.MatchesWildmat(wildmat)).ToList();
                        session.Close();
                    }
                }
                catch (MappingException mex)
                {
                    send403 = true;
                    Logger.Error("NHibernate Mapping Exception! (Is schema out of date or damaged?)", mex);
                }
                catch (Exception ex)
                {
                    send403 = true;
                    Logger.Error("Exception when trying to handle LIST", ex);
                }

                if (send403)
                {
                    await Send("403 Archive server temporarily offline\r\n");
                    return new CommandProcessingResult(true);
                }

                await Send("215 information follows\r\n");
                var epoch = new DateTime(1970, 1, 1);
                foreach (var ng in newsGroups)
                    await Send("{0} {1} {2}\r\n", ng.Name, (ng.CreateDate - epoch).TotalSeconds, ng.CreatorEntity);
                await Send(".\r\n");

                return new CommandProcessingResult(true);
            }

            if (string.Compare(content, "LIST DISTRIB.PATS\r\n", StringComparison.OrdinalIgnoreCase) == 0)
            {
                List<DistributionPattern> pats;

                using (var session = Database.SessionUtility.OpenSession())
                {
                    pats = session.Query<DistributionPattern>().ToList();
                    session.Close();
                }

                await Send("215 information follows\r\n");
                foreach (var pat in pats)
                    await Send("{0}:{1}:{2}\r\n", pat.Weight, pat.Wildmat, pat.Distribution);
                await Send(".\r\n");

                return new CommandProcessingResult(true);
            }

            if (content.StartsWith("LIST HEADERS", StringComparison.OrdinalIgnoreCase))
            {
                await Send("215 information follows\r\n:\r\n.\r\n");
                return new CommandProcessingResult(true);
            }

            if (string.Compare(content, "LIST NEWSGROUPS\r\n", StringComparison.OrdinalIgnoreCase) == 0)
            {
                IList<Newsgroup> newsGroups = null;

                var send403 = false;

                try
                {
                    using (var session = Database.SessionUtility.OpenSession())
                    {
                        newsGroups = session.Query<Newsgroup>().AddMetagroups(session, Identity).OrderBy(n => n.Name).ToList();
                        session.Close();
                    }
                }
                catch (MappingException mex)
                {
                    send403 = true;
                    Logger.Error("NHibernate Mapping Exception! (Is schema out of date or damaged?)", mex);
                }
                catch (Exception ex)
                {
                    send403 = true;
                    Logger.Error("Exception when trying to handle LIST", ex);
                }

                if (send403)
                {
                    await Send("403 Archive server temporarily offline\r\n");
                    return new CommandProcessingResult(true);
                }

                await Send("215 information follows\r\n");
                foreach (var ng in newsGroups)
                    await Send("{0}\t{1}\r\n", ng.Name, ng.Description);
                await Send(".\r\n");

                return new CommandProcessingResult(true);
            }

            if (string.Compare(content, "LIST OVERVIEW.FMT\r\n", StringComparison.OrdinalIgnoreCase) == 0)
            {
                await Send("215 Order of fields in overview database.\r\n");
                await Send("Subject:\r\n");
                await Send("From:\r\n");
                await Send("Date:\r\n");
                await Send("Message-ID:\r\n");
                await Send("References:\r\n");
                await Send(":bytes\r\n");
                await Send(":lines\r\n");
                await Send(".\r\n");

                return new CommandProcessingResult(true);
            }

            // RFC 3977 - If no keyword is provided, it defaults to ACTIVE.
            if (string.Compare(content, "LIST\r\n", StringComparison.OrdinalIgnoreCase) == 0 ||
                string.Compare(content, "LIST ACTIVE\r\n", StringComparison.OrdinalIgnoreCase) == 0 ||
                content.StartsWith("LIST ", StringComparison.OrdinalIgnoreCase) ||
                content.StartsWith("LIST ACTIVE ", StringComparison.OrdinalIgnoreCase))
            {
                IList<Newsgroup> newsGroups = null;

                string wildmat;
                if (content.EndsWith("LIST\r\n", StringComparison.OrdinalIgnoreCase) ||
                    content.EndsWith("ACTIVE\r\n", StringComparison.OrdinalIgnoreCase))
                    wildmat = null;
                else if (content.StartsWith("LIST ACTIVE ", StringComparison.OrdinalIgnoreCase))
                    wildmat = content.TrimEnd('\r', '\n').Split(' ').Skip(2).Aggregate((c, n) => c + " " + n);
                else
                    wildmat = content.TrimEnd('\r', '\n').Split(' ').Skip(1).Aggregate((c, n) => c + " " + n);

                var send403 = false;

                try
                {
                    using (var session = Database.SessionUtility.OpenSession())
                    {
                        newsGroups = wildmat == null
                            ? session.Query<Newsgroup>().AddMetagroups(session, Identity).OrderBy(n => n.Name).ToList()
                            : session.Query<Newsgroup>().AddMetagroups(session, Identity).OrderBy(n => n.Name.MatchesWildmat(wildmat)).ToList();
                        session.Close();
                    }
                }
                catch (MappingException mex)
                {
                    Logger.Error("NHibernate Mapping Exception! (Is schema out of date or damaged?)", mex);
                    send403 = true;
                }
                catch (Exception ex)
                {
                    Logger.Error("Exception when trying to handle LIST", ex);
                    send403 = true;
                }

                if (send403)
                {
                    await Send("403 Archive server temporarily offline\r\n");
                    return new CommandProcessingResult(true);
                }

                await Send("215 list of newsgroups follows\r\n");
                foreach (var ng in newsGroups)
                    await Send("{0} {1} {2} {3}\r\n", ng.Name, ng.HighWatermark ?? 0, ng.LowWatermark ?? 0, ng.Moderated ? "m" : CanPost ? "y" : "n");
                await Send(".\r\n");
                return new CommandProcessingResult(true);
            }

            await Send("501 Syntax Error\r\n");
            return new CommandProcessingResult(true);
        }

        /// <summary>
        /// Handles the LISTGROUP command from a client, which allows a client to set the currently
        /// selected newsgroup and also retrieve a list of article numbers.
        /// </summary>
        /// <param name="content">The full command request provided by the client</param>
        /// <returns>A command processing result specifying the command is handled.</returns>
        /// <remarks>See <a href="http://tools.ietf.org/html/rfc3977#section-6.1.2">RFC 3977</a> for more information.</remarks>
        private async Task<CommandProcessingResult> ListGroup(string content)
        {
            var parts = content.TrimEnd('\r', '\n').Split(' ');

            if (parts.Length == 1 && CurrentNewsgroup == null)
                await Send("412 No newsgroup selected\r\n");

            using (var session = Database.SessionUtility.OpenSession())
            {
                var name = (parts.Length == 2) ? parts[1] : CurrentNewsgroup;
                var ng = session.Query<Newsgroup>().AddMetagroups(session, Identity).SingleOrDefault(n => n.Name == name);

                if (ng == null)
                {
                    await Send("411 No such newsgroup\r\n");
                    return new CommandProcessingResult(true);
                }

                CurrentNewsgroup = ng.Name;
                if (ng.PostCount == 0)
                {
                    await Send("211 0 0 0 {0}\r\n", ng.Name);
                    return new CommandProcessingResult(true);
                }

                IList<ArticleNewsgroup> articleNewsgroups;
                if (parts.Length < 3)
                    articleNewsgroups = session.Query<ArticleNewsgroup>().Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == ng.Name).OrderBy(a => a.Number).ToList();
                else
                {
                    var range = ParseRange(parts[2]);
                    if (range == null || range.Equals(default(System.Tuple<int, int?>)))
                    {
                        await Send("501 Syntax Error\r\n");
                        return new CommandProcessingResult(true);
                    }

                    // ReSharper disable once ConvertIfStatementToConditionalTernaryExpression
                    if (!range.Item2.HasValue) // LOW-
                        articleNewsgroups = session.Query<ArticleNewsgroup>().Fetch(a => a.Newsgroup).Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == ng.Name && an.Number >= range.Item1).OrderBy(a => a.Number).ToList();
                    else // LOW-HIGH
                        articleNewsgroups = session.Query<ArticleNewsgroup>().Fetch(a => a.Newsgroup).Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == ng.Name && an.Number >= range.Item1 && an.Number <= range.Item2.Value).ToList();
                }

                session.Close();

                CurrentArticleNumber = !articleNewsgroups.Any() ? default(long?) : articleNewsgroups.First().Number;

                await Send("211 {0} {1} {2} {3}\r\n", ng.PostCount, ng.LowWatermark, ng.HighWatermark, ng.Name);
                foreach (var article in articleNewsgroups)
                    await Send("{0}\r\n", article.Number.ToString(CultureInfo.InvariantCulture));
                await Send(".\r\n");
            }

            return new CommandProcessingResult(true);
        }

        private async Task<CommandProcessingResult> Mode(string content)
        {
            if (content.StartsWith("MODE READER", StringComparison.OrdinalIgnoreCase))
            {
                await Send("200 This server is not a mode-switching server, but whatever!\r\n");
                return new CommandProcessingResult(true);
            }

            await Send("501 Syntax Error\r\n");
            return new CommandProcessingResult(true);
        }

        private async Task<CommandProcessingResult> NewGroups(string content)
        {
            var parts = content.TrimEnd('\r', '\n').Split(' ');

            var dateTime = string.Join(" ", parts.ElementAt(1), parts.ElementAt(2));
            DateTime afterDate;
            if (!(parts.ElementAt(1).Length == 8 && DateTime.TryParseExact(dateTime, "yyyyMMdd HHmmss", CultureInfo.InvariantCulture, parts.Length == 4 ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out afterDate)) &&
                !(parts.ElementAt(1).Length == 6 && DateTime.TryParseExact(dateTime, "yyMMdd HHmmss", CultureInfo.InvariantCulture, parts.Length == 4 ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out afterDate)))
            {
                await Send("501 Syntax Error\r\n");
                return new CommandProcessingResult(true);
            }

            List<Newsgroup> newsGroups;
            using (var session = Database.SessionUtility.OpenSession())
            {
                newsGroups = session.Query<Newsgroup>().AddMetagroups(session, Identity).Where(n => n.CreateDate >= afterDate).OrderBy(n => n.Name).ToList();
                session.Close();
            }

            await Send("231 List of new newsgroups follows (multi-line)\r\n");
            foreach (var ng in newsGroups)
                await Send("{0} {1} {2} {3}\r\n", ng.Name, ng.HighWatermark, ng.LowWatermark, CanPost ? "y" : "n");
            await Send(".\r\n");
            return new CommandProcessingResult(true);
        }

        /// <summary>
        /// Handles the NEWNEWS command from a client, which allows a client to request
        /// a list of articles in newsgroups matching a pattern that are new since a certain date
        /// </summary>
        /// <param name="content">The full command request provided by the client</param>
        /// <returns>A command processing result specifying the command is handled.</returns>
        /// <remarks>See <a href="http://tools.ietf.org/html/rfc3977#section-7.4">RFC 3977</a> for more information.</remarks>
        private async Task<CommandProcessingResult> NewNews(string content)
        {
            // Syntax: NEWNEWS wildmat date time [GMT]
            var parts = content.TrimEnd('\r', '\n').Split(' ');
            if (parts.Length < 4 || parts.Length > 5)
            {
                await Send("501 Syntax Error\r\n");
                return new CommandProcessingResult(true);
            }
            
            var wildmat = parts[1];

            var dateTime = string.Join(" ", parts.ElementAt(2), parts.ElementAt(3));
            DateTime afterDate;
            if (!(parts[2].Length == 8 && DateTime.TryParseExact(dateTime, "yyyyMMdd HHmmss", CultureInfo.InvariantCulture, parts.Length == 5 ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out afterDate)) &&
                !(parts[3].Length == 6 && DateTime.TryParseExact(dateTime, "yyMMdd HHmmss", CultureInfo.InvariantCulture, parts.Length == 5 ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out afterDate)))
            {
                await Send("501 Syntax Error\r\n");
                return new CommandProcessingResult(true);
            }
            afterDate = afterDate.ToUniversalTime();

            IList<Article> newArticles;
            using (var session = Database.SessionUtility.OpenSession())
            {
                newArticles = session.CreateCriteria<Article>()
                    .Add(Restrictions.Ge("DateTimeParsed", afterDate))
                    .SetFetchMode("ArticleNewsgroups", FetchMode.Join)
                    .SetProjection(Projections.ProjectionList()
                        .Add(Projections.Property("MessageId"), "MessageId"))
                    .SetResultTransformer(Transformers.AliasToBean<Article>())
                    .List<Article>();
                session.Close();
            }

            await Send("230 list of new articles by message-id follows\r\n");
            if (newArticles != null)
                foreach (var a in newArticles.Where(na => na.ArticleNewsgroups.Select(an => an.Newsgroup).Any(ng => ng.Name.MatchesWildmat(wildmat))))
                    await Send("{0}\r\n", a.MessageId);
            await Send(".\r\n");
            return new CommandProcessingResult(true);
        }

        private async Task<CommandProcessingResult> Next()
        {
            // If the currently selected newsgroup is invalid, a 412 response MUST be returned.
            if (string.IsNullOrWhiteSpace(CurrentNewsgroup))
            {
                await Send("412 No newsgroup selected\r\n");
                return new CommandProcessingResult(true);
            }

            var currentArticleNumber = CurrentArticleNumber;

            ArticleNewsgroup previousArticleNewsgroup;

            if (!currentArticleNumber.HasValue)
            {
                await Send("420 Current article number is invalid\r\n");
                return new CommandProcessingResult(true);
            }

            using (var session = Database.SessionUtility.OpenSession())
            {
                previousArticleNewsgroup = session.Query<ArticleNewsgroup>()
                    .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number > currentArticleNumber.Value)
                    .MinBy(an => an.Number);
                session.Close();
            }

            // If the current article number is already the last article of the newsgroup, a 421 response MUST be returned.
            if (previousArticleNewsgroup == null)
            {
                await Send("421 No next article in this group\r\n");
                return new CommandProcessingResult(true);
            }

            CurrentArticleNumber = previousArticleNewsgroup.Number;

            await Send("223 {0} {1} retrieved\r\n", previousArticleNewsgroup.Number, previousArticleNewsgroup.Article.MessageId);
            return new CommandProcessingResult(true);
        }

        private async Task<CommandProcessingResult> Over(string content)
        {
            var param = content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');
            
            IList<ArticleNewsgroup> articleNewsgroups = null;
            var send403 = false;

            try
            {
                using (var session = Database.SessionUtility.OpenSession())
                {
                    if (string.IsNullOrWhiteSpace(param))
                    {
                        // Third form (current article number used)
                        if (CurrentNewsgroup == null)
                        {
                            await Send("412 No news group current selected\r\n");
                            return new CommandProcessingResult(true);
                        }

                        if (CurrentArticleNumber == null)
                        {
                            await Send("420 Current article number is invalid\r\n");
                            return new CommandProcessingResult(true);
                        }

                        if (CurrentNewsgroup.EndsWith(".deleted"))
                            articleNewsgroups = session.Query<ArticleNewsgroup>()
                                .Where(an => an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Cancelled && an.Number == CurrentArticleNumber)
                                .ToArray();
                        else if (CurrentNewsgroup.EndsWith(".pending"))
                            articleNewsgroups = session.Query<ArticleNewsgroup>()
                                .Where(an => an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Pending && an.Number == CurrentArticleNumber)
                                .ToArray();
                        else
                            articleNewsgroups = session.Query<ArticleNewsgroup>()
                                .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == CurrentArticleNumber)
                                .ToArray();

                        if (!articleNewsgroups.Any())
                        {
                            await Send("420 Current article number is invalid\r\n");
                            return new CommandProcessingResult(true);
                        }
                    }
                    else if (param.StartsWith("<", StringComparison.Ordinal))
                    {
                        // First form (message-id specified)
                        articleNewsgroups = session.Query<ArticleNewsgroup>().Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Article.MessageId == param).ToArray();

                        if (!articleNewsgroups.Any())
                        {
                            await Send("430 No article with that message-id\r\n");
                            return new CommandProcessingResult(true);
                        }
                    }
                    else
                    {
                        // Second form (range specified)
                        if (CurrentNewsgroup == null)
                        {
                            await Send("412 No news group current selected\r\n");
                            return new CommandProcessingResult(true);
                        }

                        var range = ParseRange(param);
                        if (range == null || range.Equals(default(System.Tuple<int, int?>)))
                        {
                            await Send("423 No articles in that range\r\n");
                            return new CommandProcessingResult(true);
                        }

                        if (!range.Item2.HasValue) // LOW-
                        {
                            if (CurrentNewsgroup.EndsWith(".deleted"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Cancelled && an.Number >= range.Item1)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else if (CurrentNewsgroup.EndsWith(".pending"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Pending && an.Number >= range.Item1)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number >= range.Item1)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                        }
                        else // LOW-HIGH
                        {
                            if (CurrentNewsgroup.EndsWith(".deleted"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Cancelled && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else if (CurrentNewsgroup.EndsWith(".pending"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Pending && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                        }

                        if (!articleNewsgroups.Any())
                        {
                            await Send("423 No articles in that range\r\n");
                            return new CommandProcessingResult(true);
                        }
                    }

                    session.Close();
                }
            }
            catch (Exception ex)
            {
                send403 = true;
                Logger.Error("Exception when trying to handle XOVER", ex);
            }

            if (send403)
            {
                await Send("403 Archive server temporarily offline\r\n");
                return new CommandProcessingResult(true);
            }
            
            CurrentArticleNumber = articleNewsgroups.First().Number;
            Func<string, string> unfold = i => string.IsNullOrWhiteSpace(i) ? i : i.Replace("\r\n", string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

            if (Compression && CompressionGZip)
                await Send("224 Overview information follows (multi-line) [COMPRESS=GZIP]\r\n");
            else
                await Send("224 Overview information follows (multi-line)\r\n");

            var sb = new StringBuilder();

            foreach (var articleNewsgroup in articleNewsgroups)
                sb.AppendFormat(
                    "{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\r\n",
                    string.CompareOrdinal(articleNewsgroup.Newsgroup.Name, CurrentNewsgroup) == 0 ? articleNewsgroup.Number : 0,
                    unfold(articleNewsgroup.Article.Subject).Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' '),
                    unfold(articleNewsgroup.Article.From).Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' '),
                    unfold(articleNewsgroup.Article.Date).Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' '),
                    unfold(articleNewsgroup.Article.MessageId).Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' '),
                    unfold(articleNewsgroup.Article.References).Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' '),
                    unfold((articleNewsgroup.Article.Body.Length * 2).ToString(CultureInfo.InvariantCulture)),
                    unfold(articleNewsgroup.Article.Body.Split(new[] { "\r\n" }, StringSplitOptions.None).Length.ToString(CultureInfo.InvariantCulture)));
            sb.Append(".\r\n");
            await SendCompressed(sb.ToString());

            return new CommandProcessingResult(true);
        }

        /// <summary>
        /// Handles the POST command from a client, which allows a client to compose
        /// an original, new message.
        /// </summary>
        /// <returns>A command processing result specifying the command is handled.</returns>
        /// <remarks>See <a href="http://tools.ietf.org/html/rfc3977#section-6.3.1">RFC 3977</a> for more information.</remarks>
        [NotNull]
        private async Task<CommandProcessingResult> Post()
        {
            if (!CanPost)
            {
                await Send("440 Posting not permitted\r\n");
                return new CommandProcessingResult(true);
            }

            await Send("340 Send article to be posted\r\n");

            return await PostMessageAccumulator(null, null);
        }

        private async Task<CommandProcessingResult> PostMessageAccumulator(string msg, CommandProcessingResult prev)
        {
            if (
                // Message ends naturally
                    msg != null && msg.EndsWith("\r\n.\r\n", StringComparison.OrdinalIgnoreCase) ||
                // Message delimiter comes in second batch
                    (prev != null && prev.Message != null && prev.Message.EndsWith("\r\n", StringComparison.OrdinalIgnoreCase) && msg != null && msg.EndsWith(".\r\n", StringComparison.OrdinalIgnoreCase)))
            {
                var send441 = false;

                try
                {
                    Article article;
                    if (!Data.Article.TryParse(prev.Message == null ? msg.Substring(0, msg.Length - 5) : prev.Message + msg, out article))
                    {
                        await Send("441 Posting failed\r\n");
                        return new CommandProcessingResult(true, true);
                    }

                    article.ArticleNewsgroups = new HashSet<ArticleNewsgroup>();
                    article.Path = PathHost;

                    using (var session = Database.SessionUtility.OpenSession())
                    {
                        session.Save(article);

                        foreach (var newsgroupName in article.Newsgroups.Split(' '))
                        {
                            bool canApprove;
                            if (Identity == null)
                                canApprove = false;
                            else if (Identity.CanInject || Identity.CanApproveAny)
                                canApprove = true;
                            else
                                canApprove = Identity.Moderates.Any(ng => ng.Name == newsgroupName);

                            if (!canApprove)
                            {
                                article.Approved = null;
                                article.RemoveHeader("Approved");
                            }

                            if (Identity != null && !Identity.CanCancel)
                            {
                                article.Supersedes = null;
                                article.RemoveHeader("Supersedes");
                            }

                            if (Identity != null && !Identity.CanInject)
                            {
                                article.InjectionDate = DateTime.UtcNow.ToString("dd MMM yyyy HH:mm:ss") + " +0000";
                                article.ChangeHeader("Injection-Date", DateTime.UtcNow.ToString("dd MMM yyyy HH:mm:ss") + " +0000");
                                article.InjectionInfo = null;
                                article.RemoveHeader("Injection-Info");
                                article.Xref = null;
                                article.RemoveHeader("Xref");

                                // RFC 5536 3.2.6. The Followup-To header field SHOULD NOT appear in a message, unless its content is different from the content of the Newsgroups header field.
                                if (!string.IsNullOrWhiteSpace(article.FollowupTo) &&
                                    string.Compare(article.FollowupTo, article.Newsgroups, StringComparison.OrdinalIgnoreCase) == 0)
                                    article.FollowupTo = null;
                            }

                            if ((article.Control != null && Identity == null) ||
                                (article.Control != null && Identity != null && article.Control.StartsWith("cancel ", StringComparison.OrdinalIgnoreCase) && !Identity.CanCancel) ||
                                (article.Control != null && Identity != null && article.Control.StartsWith("newgroup ", StringComparison.OrdinalIgnoreCase) && !Identity.CanCreateGroup) ||
                                (article.Control != null && Identity != null && article.Control.StartsWith("rmgroup ", StringComparison.OrdinalIgnoreCase) && !Identity.CanDeleteGroup) ||
                                (article.Control != null && Identity != null && article.Control.StartsWith("checkgroups ", StringComparison.OrdinalIgnoreCase) && !Identity.CanCheckGroups))
                            {
                                await Send("480 Permission to issue control message denied\r\n");
                                return new CommandProcessingResult(true, true);
                            }

                            // Moderation - if this is a moderator's approval message, don't post it, but approve the referenced message.
                            if (canApprove && !string.IsNullOrEmpty(article.References) &&
                                (article.Body.StartsWith("APPROVE\r\n", StringComparison.OrdinalIgnoreCase) ||
                                    article.Body.StartsWith("APPROVED\r\n", StringComparison.OrdinalIgnoreCase)))
                            {
                                var references = article.References.Split(' ');

                                var target = session.CreateQuery("from ArticleNewsgroup an where an.Article.MessageId IN (:ReferencesList) AND an.Newsgroup.Name = :NewsgroupName")
                                    .SetParameterList("ReferencesList", references)
                                    .SetParameter("NewsgroupName", newsgroupName)
                                    .List<ArticleNewsgroup>()
                                    .SingleOrDefault();

                                if (target != null)
                                {
                                    target.Article.Approved = Identity.Mailbox ?? string.Format("{0}@{1}", Identity.Username, this.server.PathHost);
                                    session.SaveOrUpdate(target.Article);

                                    target.Pending = false;
                                    session.SaveOrUpdate(target);
                                    session.Flush();
                                    session.Close();

                                    await Send("240 Article received OK\r\n");
                                    return new CommandProcessingResult(true, true)
                                    {
                                        Message = prev.Message + msg
                                    };
                                }
                            }

                            var newsgroupNameClosure = newsgroupName;
                            // We don't add metagroups here, you can't 'post' directly to a meta group.
                            var newsgroup = session.Query<Newsgroup>().SingleOrDefault(n => n.Name == newsgroupNameClosure);
                            if (newsgroup == null)
                                continue;

                            var articleNewsgroup = new ArticleNewsgroup
                            {
                                Article = article,
                                Cancelled = false,
                                Newsgroup = newsgroup,
                                Number = session.CreateQuery("select max(an.Number) from ArticleNewsgroup an where an.Newsgroup.Name = :NewsgroupName").SetParameter("NewsgroupName", newsgroupName).UniqueResult<int>() + 1,
                                Pending = newsgroup.Moderated && !canApprove
                            };
                            session.Save(articleNewsgroup);

                            if (article.Control != null)
                                HandleControlMessage(newsgroup, article);
                        }

                        session.Flush();
                        session.Close();
                    }
                    
                    await Send("240 Article received OK\r\n");
                    return new CommandProcessingResult(true, true)
                    {
                        Message = prev.Message + msg
                    };
                }
                catch (Exception ex)
                {
                    Logger.Error("Exception when trying to handle POST", ex);
                    send441 = true;
                }

                if (send441)
                {
                    await Send("441 Posting failed\r\n");
                    return new CommandProcessingResult(true);
                }
            }

            return new CommandProcessingResult(true, false)
            {
                MessageHandler = PostMessageAccumulator,
                Message = prev == null ? msg : prev.Message == null ? msg : prev.Message + "\r\n" + msg
            };
        }

        /// <summary>
        /// Handles the QUIT command from a client, which shuts down the socket and destroys this object.
        /// </summary>
        /// <returns>A command processing result specifying the connection is quitting.</returns>
        /// <remarks>See <a href="http://tools.ietf.org/html/rfc3977#section-5.4">RFC 3977</a> for more information.</remarks>
        [NotNull]
        private async Task<CommandProcessingResult> Quit()
        {
            await Shutdown();
            return new CommandProcessingResult(true, true);
        }

        private async Task<CommandProcessingResult> StartTLS()
        {
            if (TLS)
            {
                await Send("502 Command unavailable\r\n");
                return new CommandProcessingResult(true);
            }

            await Send("580 Can not initiate TLS negotiation\r\n");
            return new CommandProcessingResult(true);
        }

        /// <summary>
        /// Handles the STAT command from a client, which allows a client to check for the
        /// existence of the article without retrieving the article text.
        /// </summary>
        /// <param name="content">The full command request provided by the client</param>
        /// <returns>A command processing result specifying the command is handled.</returns>
        /// <remarks>See <a href="http://tools.ietf.org/html/rfc3977#section-6.2.4">RFC 3977</a> for more information.</remarks>
        private async Task<CommandProcessingResult> Stat(string content)
        {
            var param = (string.Compare(content, "STAT\r\n", StringComparison.OrdinalIgnoreCase) == 0)
                ? null
                : content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (string.IsNullOrEmpty(param))
            {
                if (!CurrentArticleNumber.HasValue)
                {
                    await Send("430 No article with that message-id\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(CurrentNewsgroup))
                {
                    await Send("412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }
            }

            using (var session = Database.SessionUtility.OpenSession())
            {
                ArticleNewsgroup articleNewsgroup;
                int type;
                if (string.IsNullOrEmpty(param))
                {
                    articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == CurrentArticleNumber);
                    type = 3;
                }
                else if (param.StartsWith("<", StringComparison.Ordinal))
                {
                    articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Article.MessageId == param);
                    type = 1;
                }
                else
                {
                    int articleNumber;
                    if (!int.TryParse(param, out articleNumber))
                    {
                        await Send("423 No article with that number\r\n");
                        return new CommandProcessingResult(true);
                    }

                    articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == articleNumber);
                    type = 2;
                }

                session.Close();

                if (articleNewsgroup == null)
                    switch (type)
                    {
                        case 1:
                            await Send("430 No article with that message-id\r\n");
                            break;
                        case 2:
                            await Send("423 No article with that number\r\n");
                            break;
                        case 3:
                            await Send("420 Current article number is invalid\r\n");
                            break;
                    }
                else
                {
                    switch (type)
                    {
                        case 1:
                            await Send(
                                "223 {0} {1}\r\n",
                                (!string.IsNullOrEmpty(CurrentNewsgroup) && string.CompareOrdinal(articleNewsgroup.Newsgroup.Name, CurrentNewsgroup) == 0) ? articleNewsgroup.Number : 0,
                                articleNewsgroup.Article.MessageId);
                            break;
                        case 2:
                            await Send("223 {0} {1}\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId);
                            break;
                        case 3:
                            await Send("223 {0} {1}\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId);
                            break;
                    }
                }
            }

            return new CommandProcessingResult(true);
        }

        private async Task<CommandProcessingResult> XFeature(string content)
        {
            if (string.Compare(content, "XFEATURE COMPRESS GZIP TERMINATOR\r\n", StringComparison.OrdinalIgnoreCase) == 0)
            {
                Compression = true;
                CompressionGZip = true;
                CompressionTerminator = true;

                await Send("290 feature enabled\r\n");
                return new CommandProcessingResult(true);
            }

            // Not handled.
            return new CommandProcessingResult(false);
        }

        /// <summary>
        /// Handles the XHDR command from a client, which allows a client to retrieve specific article headers
        /// </summary>
        /// <param name="content">The full command request provided by the client</param>
        /// <returns>A command processing result specifying the command is handled.</returns>
        /// <remarks>See <a href="http://tools.ietf.org/html/rfc2980#section-2.6">RFC 2980</a> for more information.</remarks>
        private async Task<CommandProcessingResult> XHDR(string content)
        {
            var header = content.Split(' ')[1];
            var rangeExpression = content.Split(' ')[2].TrimEnd('\r', '\n');
            
            if (CurrentNewsgroup == null)
            {
                await Send("412 No news group current selected\r\n");
                return new CommandProcessingResult(true);
            }

            if (header == null)
            {
                await Send(".\r\n");
                return new CommandProcessingResult(true);
            }

            IList<ArticleNewsgroup> articleNewsgroups = null;

            var send403 = false;
            try
            {
                using (var session = Database.SessionUtility.OpenSession())
                {
                    var ng = session.Query<Newsgroup>().AddMetagroups(session, Identity).SingleOrDefault(n => n.Name == CurrentNewsgroup);
                    if (ng == null)
                    {
                        await Send("412 No news group current selected\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (string.IsNullOrEmpty(rangeExpression))
                    {
                        if (CurrentArticleNumber == null)
                        {
                            await Send("420 No article(s) selected\r\n");
                            return new CommandProcessingResult(true);
                        }

                        if (CurrentNewsgroup.EndsWith(".deleted"))
                            articleNewsgroups = session.Query<ArticleNewsgroup>()
                                .Where(an => an.Cancelled && an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Number == CurrentArticleNumber)
                                .OrderBy(a => a.Number)
                                .ToList();
                        else if (CurrentNewsgroup.EndsWith(".pending"))
                            articleNewsgroups = session.Query<ArticleNewsgroup>()
                                .Where(an => an.Pending && an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Number == CurrentArticleNumber)
                                .OrderBy(a => a.Number)
                                .ToList();
                        else
                            articleNewsgroups = session.Query<ArticleNewsgroup>()
                                .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == ng.Name && an.Number == CurrentArticleNumber)
                                .OrderBy(a => a.Number)
                                .ToList();
                    }
                    else
                    {
                        var range = ParseRange(rangeExpression);
                        if (range == null || range.Equals(default(System.Tuple<int, int?>)))
                        {
                            await Send("501 Syntax Error\r\n");
                            return new CommandProcessingResult(true);
                        }

                        if (!range.Item2.HasValue)
                        {
                            // LOW-
                            if (CurrentNewsgroup.EndsWith(".deleted"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Cancelled && an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Number >= range.Item1)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else if (CurrentNewsgroup.EndsWith(".pending"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Pending && an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Number >= range.Item1)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == ng.Name && an.Number >= range.Item1)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                        }
                        else
                        {
                            // LOW-HIGH
                            if (CurrentNewsgroup.EndsWith(".deleted"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Cancelled && an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else if (CurrentNewsgroup.EndsWith(".pending"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Pending && an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == ng.Name && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                        }
                    }

                    session.Close();
                }
            }
            catch (Exception ex)
            {
                send403 = true;
                Logger.Error("Exception when trying to handle XHDR", ex);
            }

            if (send403)
            {
                await Send("403 Archive server temporarily offline\r\n");
                return new CommandProcessingResult(true);
            }
            
            if (!articleNewsgroups.Any())
            {
                await Send(".\r\n");
                return new CommandProcessingResult(true);
            }

            await Send("221 Header follows\r\n");
            var sb = new StringBuilder();
            foreach (var articleNewsgroup in articleNewsgroups)
                sb.AppendFormat("{0} {1}\r\n", articleNewsgroup.Number, articleNewsgroup.Article.GetHeader(header));
            sb.Append(".\r\n");
            await this.SendCompressed(sb.ToString());

            return new CommandProcessingResult(true);
        }

        /// <summary>
        /// Handles the XOVER command from a client, which allows a client to retrieve metadata from articles.  
        /// </summary>
        /// <param name="content">The full command request provided by the client</param>
        /// <returns>A command processing result specifying the command is handled.</returns>
        /// <remarks>See <a href="http://tools.ietf.org/html/rfc2980#section-2.8">RFC 2980</a> for more information.</remarks>
        [NotNull, Pure]
        private async Task<CommandProcessingResult> XOver([NotNull] string content)
        {
            var rangeExpression = content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (CurrentNewsgroup == null)
                await Send("412 No news group current selected\r\n");
            else
            {
                Newsgroup ng = null;
                IList<ArticleNewsgroup> articleNewsgroups = null;
                var send403 = false;

                try
                {
                    using (var session = Database.SessionUtility.OpenSession())
                    {
                        ng = session.Query<Newsgroup>().AddMetagroups(session, Identity).SingleOrDefault(n => n.Name == CurrentNewsgroup);

                        if (string.IsNullOrEmpty(rangeExpression))
                        {
                            if (CurrentNewsgroup.EndsWith(".deleted"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Cancelled)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else if (CurrentNewsgroup.EndsWith(".pending"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Pending)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Newsgroup.Name == ng.Name && !an.Cancelled && !an.Pending)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                        }
                        else
                        {
                            var range = ParseRange(rangeExpression);
                            if (range == null || range.Equals(default(System.Tuple<int, int?>)))
                            {
                                await Send("501 Syntax Error\r\n");
                                return new CommandProcessingResult(true);
                            }

                            if (!range.Item2.HasValue)
                            {
                                // LOW -
                                if (CurrentNewsgroup.EndsWith(".deleted"))
                                    articleNewsgroups = session.Query<ArticleNewsgroup>()
                                        .Where(an => an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Cancelled && an.Number >= range.Item1)
                                        .OrderBy(an => an.Number)
                                        .ToList();
                                else if (CurrentNewsgroup.EndsWith(".pending"))
                                    articleNewsgroups = session.Query<ArticleNewsgroup>()
                                        .Where(an => an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Pending && an.Number >= range.Item1)
                                        .OrderBy(an => an.Number)
                                        .ToList();
                                else
                                    articleNewsgroups = session.Query<ArticleNewsgroup>()
                                        .Where(an => an.Newsgroup.Name == ng.Name && !an.Cancelled && !an.Pending && an.Number >= range.Item1)
                                        .OrderBy(an => an.Number)
                                        .ToList();
                            }
                            else
                            {
                                // LOW-HIGH
                                if (CurrentNewsgroup.EndsWith(".deleted"))
                                    articleNewsgroups = session.Query<ArticleNewsgroup>()
                                        .Where(an => an.Newsgroup.Name == ng.Name.Substring(0, CurrentNewsgroup.Length - 8) && an.Cancelled && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                        .OrderBy(an => an.Number)
                                        .ToList();
                                else if (CurrentNewsgroup.EndsWith(".deleted"))
                                    articleNewsgroups = session.Query<ArticleNewsgroup>()
                                        .Where(an => an.Newsgroup.Name == ng.Name.Substring(0, CurrentNewsgroup.Length - 8) && an.Pending && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                        .OrderBy(an => an.Number)
                                        .ToList();
                                else
                                    articleNewsgroups = session.Query<ArticleNewsgroup>()
                                        .Where(an => an.Newsgroup.Name == ng.Name && !an.Cancelled && !an.Pending && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                        .OrderBy(an => an.Number)
                                        .ToList();
                            }
                        }

                        session.Close();
                    }
                }
                catch (Exception ex)
                {
                    send403 = true;
                    Logger.Error("Exception when trying to handle XOVER", ex);
                }

                if (send403)
                {
                    await Send("403 Archive server temporarily offline\r\n");
                    return new CommandProcessingResult(true);
                }

                if (ng == null)
                {
                    await Send("411 No such newsgroup\r\n");
                    return new CommandProcessingResult(true);
                }

                if (!articleNewsgroups.Any())
                {
                    await Send("420 No article(s) selected\r\n");
                    return new CommandProcessingResult(true);
                }

                CurrentArticleNumber = articleNewsgroups.First().Number;
                Func<string, string> unfold = i => string.IsNullOrWhiteSpace(i) ? i : i.Replace("\r\n", string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

                if (Compression && CompressionGZip)
                    await Send("224 Overview information follows [COMPRESS=GZIP]\r\n");
                else
                    await Send("224 Overview information follows\r\n");

                var sb = new StringBuilder();

                foreach (var articleNewsgroup in articleNewsgroups)
                    sb.AppendFormat(
                        "{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\r\n",
                        string.CompareOrdinal(articleNewsgroup.Newsgroup.Name, CurrentNewsgroup) == 0 ? articleNewsgroup.Number : 0,
                        unfold(articleNewsgroup.Article.Subject),
                        unfold(articleNewsgroup.Article.From),
                        unfold(articleNewsgroup.Article.Date),
                        unfold(articleNewsgroup.Article.MessageId),
                        unfold(articleNewsgroup.Article.References),
                        unfold((articleNewsgroup.Article.Body.Length * 2).ToString(CultureInfo.InvariantCulture)),
                        unfold(articleNewsgroup.Article.Body.Split(new[] { "\r\n" }, StringSplitOptions.None).Length.ToString(CultureInfo.InvariantCulture)));
                sb.Append(".\r\n");
                await SendCompressed(sb.ToString());
            }

            return new CommandProcessingResult(true);
        }
        #endregion

        /// <summary>
        /// Handles a control message and makes any necessary modifications to the underlying message data store
        /// </summary>
        /// <param name="newsgroup">The newsgroup in which the control message was posted</param>
        /// <param name="article">The control message</param>
        private void HandleControlMessage([NotNull] Newsgroup newsgroup, [NotNull] Article article)
        {
            Debug.Assert(article.Control != null, "The article has no Control header, but was passed to the HandleControlMessage method.");
            Debug.Assert(Identity != null, "The article has a Control header, but the user is not authenticated.  This should have been caught by the method caller.");

            if (article.Control.StartsWith("cancel ", StringComparison.OrdinalIgnoreCase))
            {
                /* RFC 1036 3.1: Only the author of the message or the local news administrator is
                 * allowed to send this message.  The verified sender of a message is
                 * the "Sender" line, or if no "Sender" line is present, the "From"
                 * line.  The verified sender of the cancel message must be the same as
                 * either the "Sender" or "From" field of the original message.  A
                 * verified sender in the cancel message is allowed to match an
                 * unverified "From" in the original message.
                 */

                // SM: In this implementation, ONLY administrators can issue cancel messages
                Debug.Assert(Identity.CanCancel, "A cancel control header was received, but the identity lacks the CanCancel permission.  This should have been caught by the caller.");

                var messageId = article.Control.Split(' ').Skip(1).Take(1).SingleOrDefault();
                if (messageId == null || !messageId.StartsWith("<", StringComparison.Ordinal))
                    return;

                using (var session = Database.SessionUtility.OpenSession())
                {
                    var cancelTarget = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Newsgroup.Name == newsgroup.Name && an.Article.MessageId == messageId);
                    if (cancelTarget != null)
                    {
                        cancelTarget.Cancelled = true;
                        session.SaveOrUpdate(cancelTarget);
                        foreach (var an in article.ArticleNewsgroups)
                        {
                            an.Cancelled = true;
                            session.SaveOrUpdate(an);
                        }

                        session.Flush();
                        Logger.InfoFormat("{0} cancelled message {1} ({2}) in {3}", this.Identity.Username, messageId, article.Subject, cancelTarget.Newsgroup.Name);
                    }

                    session.Close();
                }
            }
        }

        [CanBeNull, Pure]
        private static System.Tuple<int, int?> ParseRange([NotNull] string input)
        {
            int low, high;
            if (input.IndexOf('-') == -1)
            {
                return !int.TryParse(input, out low) 
                    ? default(System.Tuple<int, int?>) 
                    : new System.Tuple<int, int?>(low, low);
            }

            if (input.EndsWith("-", StringComparison.Ordinal))
            {
                return !int.TryParse(input, out low) 
                    ? default(System.Tuple<int, int?>) 
                    : new System.Tuple<int, int?>(low, null);
            }

            if (!int.TryParse(input.Substring(0, input.IndexOf('-')), NumberStyles.Integer, CultureInfo.InvariantCulture, out low))
                return default(System.Tuple<int, int?>);
            if (!int.TryParse(input.Substring(input.IndexOf('-') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out high))
                return default(System.Tuple<int, int?>);

            return new System.Tuple<int, int?>(low, high);
        }
    }
}

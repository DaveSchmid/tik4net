﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace tik4net.Api
{  
    internal sealed class ApiConnection : ITikConnection
    {
        //Inspiration:
        // http://ayufan.eu/projects/rosapi/repository/entry/trunk/routeros.class.php
        // http://forum.mikrotik.com/viewtopic.php?f=9&t=31555&start=0

        private const int API_DEFAULT_PORT = 8728;
        private const int APISSL_DEFAULT_PORT = 8729;

        private readonly object _writeLockObj = new object();
        private readonly object _readLockObj = new object();
        private volatile bool _isOpened = false;
        private bool _isSsl = false;
        private bool _isOldLogin = false;
        private Encoding _encoding = Encoding.ASCII;
        private int _sendTimeout;
        private int _receiveTimeout;
        private TcpClient _tcpConnection;
        private /*NetworkStream*/System.IO.Stream _tcpConnectionStream;
        private SentenceList _readSentences = new SentenceList();

        public event EventHandler<TikConnectionCommCallbackEventArgs> OnReadRow;
        public event EventHandler<TikConnectionCommCallbackEventArgs> OnWriteRow;

        public bool IsOpened
        {
            get { return _isOpened; }
        }

        public Encoding Encoding
        {
            get { return _encoding; }
            set { _encoding = value; }
        }

        public int SendTimeout
        {
            get { return _sendTimeout; }
            set { _sendTimeout = value; }
        }

        public int ReceiveTimeout
        {
            get { return _receiveTimeout; }
            set { _receiveTimeout = value; }
        }

        public bool IsSsl
        {
            get { return _isSsl; }
        }

        public bool IsOldLogin
        {
            get { return _isOldLogin; }
        }

        public ApiConnection(bool isSsl,bool isOldLogin = false)
        {
            _isSsl = isSsl;
            _isOldLogin = isOldLogin;
        }

        private void EnsureOpened()
        {
            if (!_isOpened)
                throw new TikConnectionException("Connection has not been opened.");
        }

        public void Close()
        {            
            if (!_isSsl)
            {
                //NOTE: returns !fatal => can not use standard ExecuteNonQuery call (should not throw exception)
                var responseSentences = CallCommandSync(new string[] { "/quit" });
                //TODO should return single response of ApiFatalSentence with message "session terminated on request" - test and warning if not?
            }
            else
            {
                //NOTE: No result returned when SSL & /quit => do not read response (possible bug in SSL-API?)
                WriteCommand(new string[] { "/quit" }); 
            }
            if (_tcpConnection.Connected)
#if NET20 || NET35 || NET40 || NET45 || NET451 || NET452
                _tcpConnection.Close();
#else
                _tcpConnection.Dispose();
#endif
            _isOpened = false;        
        }

        public void Open(string host, string user, string password)
        {
            Open(host, _isSsl ? APISSL_DEFAULT_PORT : API_DEFAULT_PORT, user, password);
        }

        public void Open(string host, int port, string user, string password)
        {
#if NET20 || NET35 || NET40
            OpenSyncInternal(host, port, user, password);          
#else
            bool success;
            try
            {
                int waitMiliseconds = ReceiveTimeout > 0 ? ReceiveTimeout * 2 : 5 * 1000;
                success = OpenAsyncInternal(host, port, user, password).Wait(waitMiliseconds);
            }
            catch(Exception ex)
            {
                throw ex.InnerException; //for backward compatibility - we could not change exception types
            }

            if (!success)
                throw new TikConnectionException("Connection could not be opened.");
#endif
        }

#if !(NET20 || NET35 || NET40)
        public async System.Threading.Tasks.Task OpenAsync(string host, string user, string password)
        {
            await OpenAsync(host, _isSsl ? APISSL_DEFAULT_PORT : API_DEFAULT_PORT, user, password);
        }

        public async System.Threading.Tasks.Task OpenAsync(string host, int port, string user, string password)
        {
            await OpenAsyncInternal(host, port, user, password);
        }
#endif

#if NET20 || NET35 || NET40
        private void OpenSyncInternal(string host, int port, string user, string password)
#else
        private async System.Threading.Tasks.Task OpenAsyncInternal(string host, int port, string user, string password)
#endif
        {
            //open connection
            _tcpConnection = new TcpClient();
            if (_sendTimeout > 0)
                _tcpConnection.SendTimeout = _sendTimeout;
            if (_receiveTimeout > 0)
                _tcpConnection.ReceiveTimeout = _receiveTimeout;
#if NET20 || NET35 || NET40
            _tcpConnection.Connect(host, port);
#else
            await _tcpConnection.ConnectAsync(host, port);
#endif

            var tcpStream = _tcpConnection.GetStream();
            if (_receiveTimeout > 0)
                tcpStream.ReadTimeout = _receiveTimeout;
            if (_sendTimeout > 0)
                tcpStream.WriteTimeout = _sendTimeout;
            if (!_isSsl)
            {
                _tcpConnectionStream = tcpStream;
            }
            else
            {
                var sslStream = new SslStream(tcpStream, false,
                    new RemoteCertificateValidationCallback(ValidateServerCertificate), null);                
#if NET20 || NET35 || NET40
                sslStream.AuthenticateAsClient(host/*, cCollection, SslProtocols.Default, true*/);
#else
                await sslStream.AuthenticateAsClientAsync(host);
#endif
                _tcpConnectionStream = sslStream;
            }

            //login
            if (_isOldLogin)
                LoginOld(user, password);
            else
                Login(user, password);

            _isOpened = true;
        }

        private void Login(string user, string password)
        {
            //login connection
            ApiCommand loginCommand = new ApiCommand(this, "/login", TikCommandParameterFormat.NameValue,
                new ApiCommandParameter("name", user), new ApiCommandParameter("password", password));
            loginCommand.ExecuteNonQuery();
        }

        private void LoginOld(string user, string password)
        {
            //Get login hash
            ApiCommand readLoginHashCommand = new ApiCommand(this, "/login");
            string responseHash = readLoginHashCommand.ExecuteScalar();

            //login connection
            string hashedPass = ApiConnectionHelper.EncodePassword(password, responseHash);
            ApiCommand loginCommand = new ApiCommand(this, "/login", TikCommandParameterFormat.NameValue,
                new ApiCommandParameter("name", user), new ApiCommandParameter("response", hashedPass));
            loginCommand.ExecuteNonQuery();
        }

        private static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {            
            return true; // Accept all certificates
        }

        public void Dispose()
        {
            if (_isOpened)
                Close();
        }

        private long ReadWordLength()
        {
            byte readByte = (byte)_tcpConnectionStream.ReadByte();
            int length = 0;

            // If the first bit is set then we need to remove the first four bits, shift left 8
            // and then read another byte in.
            // We repeat this for the second and third bits.
            // If the fourth bit is set, we need to remove anything left in the first byte
            // and then read in yet another byte.
            if ((readByte & 0x80) != 0x00)
            {
                if ((readByte & 0xC0) == 0x80)
                {
                    length = ((readByte & 0x3F) << 8) + (byte)_tcpConnectionStream.ReadByte();
                }
                else
                {
                    if ((readByte & 0xE0) == 0xC0)
                    {
                        length = ((readByte & 0x1F) << 8) + (byte)_tcpConnectionStream.ReadByte();
                        length = (length << 8) + (byte)_tcpConnectionStream.ReadByte();
                    }
                    else
                    {
                        if ((readByte & 0XF0) == 0XE0)
                        {
                            length = ((readByte & 0xF) << 8) + (byte)_tcpConnectionStream.ReadByte();
                            length = (length << 8) + (byte)_tcpConnectionStream.ReadByte();
                            length = (length << 8) + (byte)_tcpConnectionStream.ReadByte();
                        }
                        else
                        {
                            length = (byte)_tcpConnectionStream.ReadByte();
                            length = (length << 8) + (byte)_tcpConnectionStream.ReadByte();
                            length = (length << 8) + (byte)_tcpConnectionStream.ReadByte();
                            length = (length << 8) + (byte)_tcpConnectionStream.ReadByte();
                        }
                    }
                }
            }
            else
            {
                length = readByte;
            }

            return length;
        }

        private string ReadWord(bool skipEmptyRow)
        {
            string result;

            do
            {
                long wordLength = ReadWordLength();

                if (wordLength < 0) //workaround (after !fatal response MinInt is returned) 
                {
                    result = "";
                    break;
                }
                else
                {
                    StringBuilder resultBuilder = new StringBuilder((int)wordLength);
                    for (int i = 0; i < wordLength; i++)
                    {
                        byte readByte = (byte)_tcpConnectionStream.ReadByte();
                        resultBuilder.Append(Encoding.GetChars(new byte[] { readByte }));
                    }

                    result = resultBuilder.ToString();
                }
            } while (skipEmptyRow && StringHelper.IsNullOrWhiteSpace(result));            

            if (OnReadRow != null)
                OnReadRow(this, new TikConnectionCommCallbackEventArgs(result));
            return result;
        }

        private ITikSentence ReadSentence()
        {
            string sentenceName = ReadWord(true);

            List<string> sentenceWords = new List<string>();
            string sentenceWord;
            do
            {
                sentenceWord = ReadWord(false);
                if (!StringHelper.IsNullOrWhiteSpace(sentenceWord)) //read ending empty row, but skip it from result
                    sentenceWords.Add(sentenceWord);
            } while (!StringHelper.IsNullOrWhiteSpace(sentenceWord));

            switch (sentenceName)
            {
                case "!done": return new ApiDoneSentence(sentenceWords);
                case "!trap": return new ApiTrapSentence(sentenceWords);
                case "!re": return new ApiReSentence(sentenceWords);
                case "!fatal": return new ApiFatalSentence(sentenceWords);
                default: throw new NotImplementedException(string.Format("Response type '{0}' not supported", sentenceName));
            }
        }

        private void WriteCommand(IEnumerable<string> commandRows)
        {
            foreach (string row in commandRows)
            {
                byte[] bytes = _encoding.GetBytes(row.ToCharArray());
                byte[] length = ApiConnectionHelper.EncodeLength(bytes.Length);

                _tcpConnectionStream.Write(length, 0, length.Length); //write length of comming sentence
                _tcpConnectionStream.Write(bytes, 0, bytes.Length);   //write sentence body

                if (OnWriteRow != null)
                    OnWriteRow(this, new TikConnectionCommCallbackEventArgs(row));
            }

            _tcpConnectionStream.WriteByte(0); //final zero byte
        }

        private ITikSentence GetOne(string tag)
        {
            do
            {
                ITikSentence result;
                // try to find in in _readSentences
                if (_readSentences.TryDequeue(tag, out result)) // found => removed from _readSentences and return as result
                    return result;

                lock (_readLockObj)
                {
                    // again - try to find in in _readSentences (could be added between last try and lock)  (see double check lock pattern)
                    if (_readSentences.TryDequeue(tag, out result)) // found => removed from _readSentences and return as result
                        return result;

                    ITikSentence sentenceFromTcp = ReadSentence();
                    if (sentenceFromTcp.Tag == tag)
                    {
                        return sentenceFromTcp;
                    }
                    else // another tag => add to _readSentences for another reading thread
                    {
                        _readSentences.Enqueue(sentenceFromTcp);
                    }
                }
            } while (true); //TODO max attempts???  //repeat until get any response for specific tag
        }
        
        private IEnumerable<ITikSentence> GetAll(string tag)
        {
            ITikSentence sentence;
            do
            {
                sentence = GetOne(tag);
                yield return sentence;
            } while (!(sentence is ApiDoneSentence || sentence is ApiFatalSentence /*|| sentence is ApiTrapSentence */)); // read sentences via TryGetOne(wait) for TAG until !done or !fatal is returned
            //NOTE both !trap and !fatal are followed with !done
        }

        public IEnumerable<ITikSentence> CallCommandSync(params string[] commandRows)
        {
            lock (_writeLockObj)
            {
                WriteCommand(commandRows);
            }
            return GetAll(string.Empty).ToList();
        }

        public IEnumerable<ITikSentence> CallCommandSync(IEnumerable<string> commandRows)
        {
            lock (_writeLockObj)
            {
                WriteCommand(commandRows);
            }
            return GetAll(string.Empty).ToList();
        }

        public Thread CallCommandAsync(IEnumerable<string> commandRows, string tag, 
            Action<ITikSentence> oneResponseCallback)
        {            
            Guard.ArgumentNotNullOrEmptyString(tag, "tag");

            commandRows = commandRows.Concat(new string[] { string.Format("{0}={1}", TikSpecialProperties.Tag, tag) });
            lock (_writeLockObj)
            {
                WriteCommand(commandRows);
            }

            Thread result = new Thread(() =>
            {
                try
                {
                    ITikSentence sentence;
                    do
                    {
                        sentence = GetOne(tag);
                        try
                        {
                            oneResponseCallback(sentence);
                        }
                        catch
                        {
                            //Do not crash reading thread because of implementation error in called code
                        }
                    } while (_isOpened && !(sentence is ApiDoneSentence /*|| sentence is ApiTrapSentence*/ || sentence is ApiFatalSentence)); // read sentences via TryGetOne(wait) for TAG until !done or !fatal is returned
                    //NOTE: Should be ended via !done or !trap+!done (called via Cancel() command for specific tag)
                }
                catch
                {
                    //catch all exceptions from GetOne -> thread should end via !done read sentence
                    //TODO: implement "timeoutException" for GetOne and cancell gracefully thraed if this exception happens
                }
            });
            result.IsBackground = true;
            result.Start();

            return result;
        }

        public ITikCommand CreateCommand()
        {
            return new ApiCommand(this);
        }

        public ITikCommand CreateCommand(TikCommandParameterFormat defaultParameterFormat)
        {
            var result = CreateCommand();
            result.DefaultParameterFormat = defaultParameterFormat;

            return result;
        }


        public ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            return new ApiCommand(this, commandText, parameters);
        }

        public ITikCommand CreateCommand(string commandText, TikCommandParameterFormat defaultParameterFormat, params ITikCommandParameter[] parameters)
        {
            var result = CreateCommand(commandText, parameters);
            result.DefaultParameterFormat = defaultParameterFormat;

            return result;
        }


        public ITikCommand CreateCommandAndParameters(string commandText, params string[] parameterNamesAndValues)
        {
            var result = new ApiCommand(this, commandText);
            result.AddParameterAndValues(parameterNamesAndValues);

            return result;
        }

        public ITikCommand CreateCommandAndParameters(string commandText, TikCommandParameterFormat defaultParameterFormat, params string[] parameterNamesAndValues)
        {
            var result = CreateCommandAndParameters(commandText, parameterNamesAndValues);
            result.DefaultParameterFormat = defaultParameterFormat;

            return result;
        }

        public ITikCommandParameter CreateParameter(string name, string value)
        {
            return new ApiCommandParameter(name, value);
        }

        public ITikCommandParameter CreateParameter(string name, string value, TikCommandParameterFormat parameterFormat)
        {
            var result = CreateParameter(name, value);
            result.ParameterFormat = parameterFormat;

            return result;
        }
    }
}

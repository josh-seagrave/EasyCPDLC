﻿/*  EASYCPDLC: CPDLC Client for the VATSIM Network
    Copyright (C) 2021 Joshua Seagrave joshseagrave@googlemail.com

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using Microsoft.Win32;
using System;
using System.IO;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Reflection;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Octokit;
using NLog;

namespace EasyCPDLC
{
    public partial class MainForm : Form
    {
        
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;
        private const int cGrip = 16;
        private const int cCaption = 32;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        private static Logger Logger = LogManager.GetCurrentClassLogger();

        public PilotData userVATSIMData;
        private Pilots pilotList;

        private static readonly HttpClient webclient = new HttpClient();
        private string logonCode;
        private int cid;
        private string callsign;
        
        private RequestForm rForm;
        private TelexForm tForm;
        private SettingsForm sForm;

        public bool stayOnTop
        {
            get
            {
                return Properties.Settings.Default.StayOnTop;
            }
            set
            {
                Properties.Settings.Default.StayOnTop = value;
                this.TopMost = value;
            }
        }

        public bool playSound
        {
            get
            {
                return Properties.Settings.Default.PlayAudibleAlert;
            }
            set
            {
                Properties.Settings.Default.PlayAudibleAlert = value;
            }
        }

        public int messageOutCounter = 1;
        private bool _connected;
        public bool connected
        {
            get
            {
                return _connected;
            }
            set
            {
                _connected = value;
                if (_connected)
                {
                    retrieveButton.Text = "DISCONNECT";
                    atcButton.Enabled = true;
                    telexButton.Enabled = true;
                }
                else
                {
                    retrieveButton.Text = "CONNECT";
                    atcButton.Enabled = false;
                    telexButton.Enabled = false;
                }
            }
        }

        public string pendingLogon = null;
        private string _currentATCUnit;
        public string currentATCUnit
        {
            get
            {
                return _currentATCUnit;
            }
            set
            {
                _currentATCUnit = value;
                try
                {
                    if (_currentATCUnit is null)
                    {
                        atcUnitDisplay.Text = "----";
                        rForm.needsLogon = true;
                        
                    }
                    else
                    {
                        atcUnitDisplay.Text = _currentATCUnit;
                        rForm.needsLogon = false;
                        
                    }
                }
                catch (NullReferenceException)
                {

                }
            }
        }

        public Font controlFont = new Font("Oxygen", 10.0f, FontStyle.Regular);
        public Font controlFontBold = new Font("Oxygen", 10.0f, FontStyle.Bold);
        public Font textFont = new Font("B612 Mono", 10.0f, FontStyle.Regular);
        public Font textFontBold = new Font("B612 Mono", 12.5f, FontStyle.Bold);
        public Font dataEntryFont = new Font("B612 Mono", 11.0f, FontStyle.Regular);
        public Color controlBackColor = Color.FromArgb(5, 5, 5);
        public Color controlFrontColor = SystemColors.ControlLight;
       
        private ContextMenuStrip popupMenu = new ContextMenuStrip();
        private ToolStripMenuItem replyMenu;
        ToolStripMenuItem wilcoMenu;
        ToolStripMenuItem rogerMenu;
        ToolStripMenuItem affirmativeMenu;
        ToolStripMenuItem negativeMenu;
        ToolStripMenuItem standbyMenu;
        ToolStripMenuItem unableMenu;
        ToolStripMenuItem deleteMenu;
        ToolStripMenuItem deleteAllMenu;
        ToolStripMenuItem freeTextMenu;
        
        private SoundPlayer player = new SoundPlayer(Properties.Resources.notification);
        private RegistryKey regKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\EasyCPDLC");

        private static Regex hoppieParse = new Regex(@"{(.*?)}");
        private static Regex cpdlcHeaderParse = new Regex(@"(\/\s*)\w*");
        private static Regex cpdlcSenderParse = new Regex(@"[/ ]([a-zA-Z]{4})\s");
        private static Regex cpdlcUnitParse = new Regex(@"_@([\w]*)@_");

        private static readonly TimeSpan updateTimer = TimeSpan.FromSeconds(10);
        private CancellationTokenSource requestCancellationTokenSource;
        private CancellationToken requestCancellationToken;

        public MainForm()
        {
            var config = new NLog.Config.LoggingConfiguration();
            var logFile = new NLog.Targets.FileTarget("logfile") { FileName ="EasyCPDLCLog.txt" };
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, logFile);
            LogManager.Configuration = config;

            Logger.Info("Logging initialised, beginning setup");

            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

            InitializeComponent();
            this.TopMost = stayOnTop;
            this.FormBorderStyle = FormBorderStyle.None;
            this.DoubleBuffered = true;
            this.SetStyle(ControlStyles.ResizeRedraw, true);
            currentATCUnit = null;
            ServicePointManager.Expect100Continue = true;
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            if (new AssemblyName(args.Name).Name == "System.Runtime.CompilerServices.Unsafe")
                return Assembly.LoadFrom(
                    Path.Combine(System.Windows.Forms.Application.StartupPath, "System.Runtime.CompilerServices.Unsafe.dll"));
            throw new Exception();
        }
        private void MainForm_Load(object sender, EventArgs e)
        {
            outputTable.BorderStyle = BorderStyle.FixedSingle;
            outputTable.HorizontalScroll.Maximum = 0;
            outputTable.AutoScroll = false;
            outputTable.VerticalScroll.Visible = false;
            outputTable.AutoScroll = true;

            CheckNewVersion();
            InitialisePopupMenu();
            ShowSetupForm();
            Setup();

            Logger.Info("Setup completed successfully");
        }

        private async void CheckNewVersion()
        {
            try
            {
                var client = new GitHubClient(new ProductHeaderValue("EasyCPDLC"));
                var releases = await client.Repository.Release.GetAll("josh-seagrave", "EasyCPDLC");
                var latest = releases[0];
                string latestVersion = latest.TagName.Replace("cpdlc", "");
                if (latestVersion != System.Windows.Forms.Application.ProductVersion)
                {
                    DialogResult updateBox = MessageBox.Show(String.Format("New Version {0} Available to download from Github. This application will now exit. Would you like me to take you to the Github page for the latest release?", latestVersion), "New Version Available", MessageBoxButtons.YesNo);
                    if (updateBox == DialogResult.Yes)
                    {
                        System.Diagnostics.Process.Start(latest.HtmlUrl);
                    }
                    System.Windows.Forms.Application.Exit();
                }
            }
            
            catch
            {

            }
        }

        private ToolStripMenuItem CreateMenuItem(string name)
        {
            ToolStripMenuItem _temp = new ToolStripMenuItem(name);
            _temp.BackColor = controlBackColor;
            _temp.ForeColor = controlFrontColor;
            _temp.Font = controlFont;
            _temp.TextAlign = ContentAlignment.TopLeft;

            return _temp;
        }
        private void InitialisePopupMenu()
        {
            popupMenu.BackColor = controlBackColor;
            popupMenu.ForeColor = controlFrontColor;
            popupMenu.Font = controlFont;
            popupMenu.ShowImageMargin = false;

            replyMenu = CreateMenuItem("REPLY");

            rogerMenu = CreateMenuItem("ROGER");
            rogerMenu.Click += RogerMessage;

            wilcoMenu = CreateMenuItem("WILCO");
            wilcoMenu.Click += WilcoMessage;

            unableMenu = CreateMenuItem("UNABLE");
            unableMenu.Click += UnableMessage;

            affirmativeMenu = CreateMenuItem("AFFIRMATIVE");
            affirmativeMenu.Click += AffirmativeMessage;

            negativeMenu = CreateMenuItem("NEGATIVE");
            negativeMenu.Click += NegativeMessage;

            standbyMenu = CreateMenuItem("STANDBY");
            standbyMenu.Click += StandbyMessage;

            freeTextMenu = CreateMenuItem("FREE TEXT");
            freeTextMenu.Click += freeTextMessage;

            deleteMenu = CreateMenuItem("DELETE");
            deleteMenu.Click += DeleteElement;

            deleteAllMenu = CreateMenuItem("DELETE ALL");
            deleteAllMenu.Click += DeleteAllElement;

            Logger.Info("Login menu initialised");
        }

        private Control SenderToControl(object sender)
        {
            ToolStripDropDownItem _sender = (ToolStripDropDownItem)sender;
            ContextMenuStrip menu = (ContextMenuStrip)_sender.DropDown.OwnerItem.OwnerItem.Owner;
            Control sourceControl = menu.SourceControl;
            return sourceControl;
        }

        private void freeTextMessage(object sender, EventArgs e)
        {
            Control sourceControl = SenderToControl(sender);
            CPDLCMessage message = (CPDLCMessage)sourceControl;

            tForm = new TelexForm(this, message.recipient);
            tForm.TopMost = stayOnTop;
            tForm.Show();
        }
        private async void RogerMessage(object sender, EventArgs e)
        {
            Control sourceControl = SenderToControl(sender);
            CPDLCMessage message = (CPDLCMessage)sourceControl;

            message.acknowledged = true;
            message.ForeColor = controlFrontColor;
            message.header.responseID = messageOutCounter;
            await SendCPDLCMessage(message.recipient, message.type, String.Format("/data2/{0}/{1}/N/ROGER", messageOutCounter, message.header.messageID));
            messageOutCounter += 1;
        }

        private async void WilcoMessage(object sender, EventArgs e)
        {
            Control sourceControl = SenderToControl(sender);
            CPDLCMessage message = (CPDLCMessage)sourceControl;

            message.acknowledged = true;
            message.ForeColor = controlFrontColor;
            await SendCPDLCMessage(message.recipient, message.type, String.Format("/data2/{0}/{1}/N/WILCO", messageOutCounter, message.header.messageID));
            messageOutCounter += 1;
        }

        private async void StandbyMessage(object sender, EventArgs e)
        {
            Control sourceControl = SenderToControl(sender);
            CPDLCMessage message = (CPDLCMessage)sourceControl;
            await SendCPDLCMessage(message.recipient, message.type, String.Format("/data2/{0}/{1}/N/STANDBY", messageOutCounter, message.header.messageID));
            messageOutCounter += 1;
        }

        private async void UnableMessage(object sender, EventArgs e)
        {
            Control sourceControl = SenderToControl(sender);
            CPDLCMessage message = (CPDLCMessage)sourceControl;

            message.acknowledged = true;
            message.ForeColor = controlFrontColor;
            await SendCPDLCMessage(message.recipient, message.type, String.Format("/data2/{0}/{1}/N/UNABLE", messageOutCounter, message.header.messageID));
            messageOutCounter += 1;
        }

        private async void AffirmativeMessage(object sender, EventArgs e)
        {
            Control sourceControl = SenderToControl(sender);
            CPDLCMessage message = (CPDLCMessage)sourceControl;

            message.acknowledged = true;
            message.ForeColor = controlFrontColor;
            await SendCPDLCMessage(message.recipient, message.type, String.Format("/data2/{0}/{1}/N/AFFIRMATIVE", messageOutCounter, message.header.messageID));
            messageOutCounter += 1;
        }

        private async void NegativeMessage(object sender, EventArgs e)
        {
            Control sourceControl = SenderToControl(sender);
            CPDLCMessage message = (CPDLCMessage)sourceControl;

            message.acknowledged = true;
            message.ForeColor = controlFrontColor;
            await SendCPDLCMessage(message.recipient, message.type, String.Format("/data2/{0}/{1}/N/NEGATIVE", messageOutCounter, message.header.messageID));
            messageOutCounter += 1;
        }

        private void ShowSetupForm()
        {

            Logger.Info("Login Form Displayed");

            DataEntry dataEntry = new DataEntry(this, regKey.GetValue("hoppieCode"), regKey.GetValue("vatsimCID"));

            if (dataEntry.ShowDialog(this) == DialogResult.OK)
            {
                logonCode = dataEntry.hoppieLogonCode;
                cid = dataEntry.vatsimCID;
                if (dataEntry.remember)
                {
                    Logger.Info("REMEMBER ME: TRUE. REGISTRY SET.");
                    regKey.SetValue("hoppieCode", logonCode);
                    regKey.SetValue("vatsimCID", cid);
                }
                else
                {
                    Logger.Info("REMEMBER ME: FALSE. REGISTRY CLEARED.");
                    if (!(regKey.GetValue("hoppieCode") is null))
                    {
                        regKey.DeleteValue("hoppieCode");
                    }
                    if (!(regKey.GetValue("vatsimCID") is null))
                    {
                        regKey.DeleteValue("vatsimCID");
                    }

                }
            }
            else
            {
                Logger.Info("Goodbye");
                LogManager.Shutdown();
                System.Windows.Forms.Application.Exit();
            }
        }
        private void Setup()
        {
            retrieveButton.Enabled = true;
            Logger.Info("Setup Complete.");
        }
        private async Task PeriodicCheckMessage(TimeSpan interval, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Logger.Debug("Attempting to poll Hoppie for new messages");

                await SendCPDLCMessage("NONE", "poll", "");
                await Task.Delay(interval, cancellationToken);
            }
        }
        public async Task SendCPDLCMessage(string recipient, string messageType, string packetData, bool _outbound = true, bool _write = true)
        {
            var connectionValues = new Dictionary<string, string> {
                {"logon", logonCode},
                {"from", callsign},
                {"to", recipient},
                {"type", messageType},
                {"packet", packetData}
            };

            var content = new FormUrlEncodedContent(connectionValues);
            try
            {
                var response = await webclient.PostAsync("http://www.hoppie.nl/acars/system/connect.html", content);
                Logger.Debug(String.Format("PACKET SENT: {0} | {1} | {2} | {3} | {4}", recipient, messageType, packetData, _outbound, _write));
                var responseString = await response.Content.ReadAsStringAsync();
                string printString = responseString.ToString().ToUpper();

                Logger.Debug("RECEIVED: " + responseString);

                if (_write)
                {
                    if (messageType == "CPDLC" && _outbound)
                    {
                        WriteMessage(packetData.Split('/').Last(), messageType, recipient, _outbound);
                    }
                    else if (messageType != "poll")
                    {
                        WriteMessage(packetData, messageType, recipient, _outbound);
                    }
                }

                if (printString != "OK")
                {
                    await TelexParser(printString);

                }
            }

            catch (Exception e)
            {
                Logger.Error(String.Format("{0}: {1}", e.GetType().FullName, e.Message));
                WriteMessage("ERROR CHECKING FOR NEW MESSAGES. RETRYING...", "SYSTEM", "SYSTEM");
            }

            return;

        }
        private CPDLCMessage CreateCPDLCMessage(string _text, string _type, string _recipient, bool _outbound = false, CPDLCResponse _header = null)
        {
            CPDLCMessage _message = new CPDLCMessage(_type, _recipient, _outbound, _header);
            // Size maxSize = new Size();
            //maxSize.Width = 420;
            //_message.MaximumSize = maxSize;
            //_message.Width = 420;
            _message.AutoSize = true;
            _message.BackColor = controlBackColor;
            _message.ForeColor = Color.Orange;
            _message.Font = textFont;
            _message.Text = _text;
            _message.BorderStyle = BorderStyle.None;
            _message.MouseDown += MessageClicked;
            _message.Margin = new Padding(0, 3, 0, 0);

            return _message;
        }

        private System.Windows.Forms.Label CreateLabel(string _text)
        {
            System.Windows.Forms.Label _message = new System.Windows.Forms.Label();
            Size maxSize = new Size();
            maxSize.Width = 65;
            _message.MaximumSize = maxSize;
            _message.Width = 65;
            _message.AutoSize = true;
            _message.BackColor = controlBackColor;
            _message.ForeColor = SystemColors.ControlDark;
            _message.Font = textFont;
            _message.Text = _text;
            _message.BorderStyle = BorderStyle.None;
            _message.Margin = new Padding(5, 3, 0, 0);



            return _message;
        }
        private void DeleteElement(object sender, EventArgs e)
        {
            ToolStripItem _sender = (ToolStripItem)sender;
            ContextMenuStrip menu = (ContextMenuStrip)_sender.Owner;
            Control sourceControl = menu.SourceControl;

            TableLayoutHelper.RemoveArbitraryRow(outputTable, outputTable.GetPositionFromControl(sourceControl).Row);
        }

        private void DeleteAllElement(object sender, EventArgs e)
        {
            outputTable.Controls.Clear();
        }
        private void MessageClicked(object sender, EventArgs e)
        {
            MouseEventArgs me = (MouseEventArgs)e;

            CPDLCMessage _sender = (CPDLCMessage)sender;

            if (_sender.type != "CPDLC" || _sender.outbound || _sender.header.responses == "NE")
            {
                _sender.ForeColor = SystemColors.ControlDark;
            }
            if (me.Button == MouseButtons.Right)
            {
                popupMenu.Items.Clear();
                popupMenu.Items.Add(deleteMenu);
                popupMenu.Items.Add(deleteAllMenu);
                deleteMenu.Enabled = true;
                if (_sender.type == "CPDLC" && !_sender.outbound && !_sender.acknowledged)
                {
                    deleteMenu.Enabled = false;
                }
                Console.WriteLine(_sender.type);
                if (_sender.type != "SYSTEM" && !_sender.acknowledged && !_sender.outbound)
                {
                    popupMenu.Items.Add(replyMenu);
                    
                    if(_sender.type != "TELEX")
                    {
                        switch (_sender.header.responses)
                        {
                            case "WU":
                                replyMenu.DropDownItems.Add(wilcoMenu);
                                replyMenu.DropDownItems.Add(unableMenu);
                                replyMenu.DropDownItems.Add(standbyMenu);
                                break;

                            case "AN":
                                replyMenu.DropDownItems.Add(affirmativeMenu);
                                replyMenu.DropDownItems.Add(negativeMenu);
                                replyMenu.DropDownItems.Add(standbyMenu);
                                break;

                            case "R":
                                replyMenu.DropDownItems.Add(rogerMenu);
                                break;

                            default:
                                break;

                        }
                    }

                    replyMenu.DropDownItems.Add(freeTextMenu);
                }

                popupMenu.Show(_sender, _sender.PointToClient(Cursor.Position));
            }
        }

        private async Task TelexParser(string response)
        {
            var responses = hoppieParse.Matches(response);

            foreach (Match _response in responses)
            {
                string format_response = "";
                string[] _modify = _response.Groups[1].Value.Replace("}", "").Split('{');
                string sender = _modify[0].Split(' ')[0];
                string type = _modify[0].Split(' ')[1];

                for (int i = 0; i < _modify.Length; i++)
                {
                    if (i > 0 && _modify[i].Length > 2)
                    {
                        if (_modify[1].StartsWith("/DATA2/"))
                        {
                            Logger.Debug("CPDLC Message identified, attempting to parse");
                            await CPDLCParser(_modify[1], sender);
                            break;
                        }

                        format_response += _modify[1];
                        WriteMessage(format_response, type, sender);
                        FlashWindow.Flash(this);
                    }
                }

            }
            return;
        }

        private async Task CPDLCParser(string _response, string _sender)
        {
            var unit = cpdlcUnitParse.Match(_response);
            if (unit.Success)
            {
                currentATCUnit = unit.Value.Trim('_', '@');
            }

            var responses = cpdlcHeaderParse.Matches(_response);
            CPDLCResponse header = new CPDLCResponse();
            header.dataType = responses[0].Value.Trim('/');
            header.messageID = Convert.ToInt32(responses[1].Value.Trim('/'));
            header.responseID = responses[2].Value.Trim('/').Length < 1 ? 0 : Convert.ToInt32(responses[2].Value.Trim('/'));
            header.responses = responses[3].Value.Trim('/');

            string[] messageContent = _response.Split(new string[] { header.responses + "/" }, StringSplitOptions.None);
            string messageString = "";
            if (messageContent[1].Contains(callsign))
            {
                messageString = messageContent[1].Split(new string[] { callsign }, StringSplitOptions.None).Last();
            }
            else
            {
                messageString = messageContent[1];
            }
            if (messageString.StartsWith("HANDOVER"))
            {
                string nextATCUnit = messageString.Split(' ').Last().Trim('@').Trim();
                await SendCPDLCMessage(nextATCUnit, "CPDLC", String.Format("/data2/{0}//Y/REQUEST LOGON", messageOutCounter), true, false);
                messageOutCounter += 1;
            }
            if (messageString.StartsWith("LOGON ACCEPTED"))
            {
                currentATCUnit = pendingLogon;
            }
            string message = callsign + " " + messageString.Replace("@@", "N/A").Replace("@", Environment.NewLine).Replace("_", "");
            message = Regex.Replace(message, @"\s+", " ");

            Logger.Debug(message);

            if (message.Contains("LOGOFF"))
            {
                currentATCUnit = null;
                pendingLogon = null;
            }

            WriteMessage(message, "CPDLC", _sender, false, header);

            player.Play();
            FlashWindow.Flash(this);

            return;
        }

        public void WriteMessage(string _response, string _type, string _recipient, bool _outbound = false, CPDLCResponse _header = null)
        {

            CPDLCMessage message;
            if (_outbound)
            {
                message = CreateCPDLCMessage(callsign + ": " + _recipient + " " + _response + ".", _type, _recipient, _outbound, _header);
            }
            else
            {
                message = CreateCPDLCMessage(_recipient + ": " + _response + ".", _type, _recipient, _outbound, _header);
                if (playSound && _recipient != "SYSTEM") { player.Play(); }
            }

            Logger.Debug("Writing message: " + _response);

            outputTable.Invoke(new Action(() => outputTable.Controls.Add(CreateLabel(DateTime.Now.ToString("HH:mm")), 0, outputTable.RowCount - 1)));
            outputTable.Invoke(new Action(() => outputTable.Controls.Add(message, 1, outputTable.RowCount - 1)));
            outputTable.Invoke(new Action(() => outputTable.RowCount += 1));
            outputTable.Invoke(new Action(() => outputTable.RowStyles.Add(new RowStyle(SizeType.AutoSize))));
            outputTable.Invoke(new Action(() => outputTable.ScrollControlIntoView(message)));
        }

        public async Task ArtificialDelay(string _message, string _type, string _sender, int _minDelay = 5, int _maxDelay = 15)
        {
            Thread.Sleep(new Random().Next(_minDelay, _maxDelay) * 1000);
            WriteMessage(_message, _type, _sender);
        }
        private void exitButton_Click(object sender, EventArgs e)
        {
            try
            {
                requestCancellationTokenSource.Cancel();
            }
            catch (NullReferenceException) { }
            this.Close();

            LogManager.Shutdown();
            System.Windows.Forms.Application.Exit();
        }
        private void MoveWindow(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }
        private async void retrieveButton_Click(object sender, EventArgs e)
        {
            string response = "";

            if (!connected)
            {
                using (WebClient wc = new WebClient())
                {
                    var json = wc.DownloadString("https://data.vatsim.net/v3/vatsim-data.json");
                    pilotList = JsonSerializer.Deserialize<Pilots>(json);

                    Logger.Debug("VATSIM Data Retrieved and Parsed");
                }

                try
                {

                    userVATSIMData = pilotList.pilots.Where(i => i.cid == cid).FirstOrDefault();
                    response = "DETAILS RETRIEVED FOR " + userVATSIMData.callsign;
                    callsign = userVATSIMData.callsign;

                    connected = true;

                    requestCancellationTokenSource = new CancellationTokenSource();
                    requestCancellationToken = requestCancellationTokenSource.Token;
                    _ = PeriodicCheckMessage(updateTimer, requestCancellationToken);
                }
                catch (NullReferenceException)
                {
                    response = "ERROR RETRIEVING DETAILS. ENSURE A FLIGHT PLAN HAS BEEN FILED AND TRY AGAIN";
                    atcButton.Enabled = false;
                    telexButton.Enabled = false;
                    connected = false;
                }
                finally
                {
                    WriteMessage(response, "SYSTEM", "SYSTEM");
                }
            }

            else
            {
                if (!(currentATCUnit is null))
                {
                    await SendCPDLCMessage(currentATCUnit, "CPDLC", String.Format("/data2/{0}//N/LOGOFF", messageOutCounter), true, false);
                }
                requestCancellationTokenSource.Cancel();
                callsign = "";
                response = "DISCONNECTED CLIENT";
                pilotList = new Pilots();
                userVATSIMData = new PilotData();

                atcButton.Enabled = false;
                telexButton.Enabled = false;
                connected = false;

                WriteMessage(response, "SYSTEM", "SYSTEM");

            }
        }
        private void telexButton_Click(object sender, EventArgs e)
        {
            tForm = new TelexForm(this);
            tForm.Show();
        }
        private void requestButton_Click(object sender, EventArgs e)
        {
            rForm = new RequestForm(this);
            rForm.TopMost = stayOnTop;
            rForm.Show();
        }

        private async void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!(currentATCUnit is null))
            {
                await SendCPDLCMessage(currentATCUnit, "CPDLC", String.Format("/data2/{0}//N/LOGOFF", messageOutCounter), true, false);
                requestCancellationTokenSource.Cancel();
            }
        }

        private void outputTable_Click(object sender, EventArgs e)
        {
            MouseEventArgs me = (MouseEventArgs)e;

            TableLayoutPanel _sender = (TableLayoutPanel)sender;

            if (me.Button == MouseButtons.Right)
            {
                popupMenu.Items.Clear();
                popupMenu.Items.Add(deleteAllMenu);

                popupMenu.Show(_sender, _sender.PointToClient(Cursor.Position));
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84)
            {  // Trap WM_NCHITTEST
                Point pos = new Point(m.LParam.ToInt32());
                pos = this.PointToClient(pos);
                if (pos.Y < cCaption)
                {
                    m.Result = (IntPtr)2;  // HTCAPTION
                    return;
                }
                if (pos.X >= this.ClientSize.Width - cGrip && pos.Y >= this.ClientSize.Height - cGrip)
                {
                    m.Result = (IntPtr)17; // HTBOTTOMRIGHT
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private void settingsButton_Click(object sender, EventArgs e)
        {
            bool[] settings = new bool[] { stayOnTop, playSound };
            sForm = new SettingsForm(this, settings);
            sForm.TopMost = stayOnTop;
            if (sForm.ShowDialog(this) == DialogResult.OK)
            {
                settings = sForm.settings;
                stayOnTop = settings[0];
                playSound = settings[1];
                Properties.Settings.Default.Save();
            }
        }
    }
    internal class NoHighlightRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.OwnerItem == null)
            {
                base.OnRenderMenuItemBackground(e);
            }
        }
    }
    public static class TableLayoutHelper
    {
        public static void RemoveArbitraryRow(TableLayoutPanel panel, int rowIndex)
        {
            if (rowIndex >= panel.RowCount)
            {
                return;
            }

            // delete all controls of row that we want to delete
            for (int i = 0; i < panel.ColumnCount; i++)
            {
                var control = panel.GetControlFromPosition(i, rowIndex);
                panel.Controls.Remove(control);
            }

            // move up row controls that comes after row we want to remove
            for (int i = rowIndex + 1; i < panel.RowCount; i++)
            {
                for (int j = 0; j < panel.ColumnCount; j++)
                {
                    var control = panel.GetControlFromPosition(j, i);
                    if (control != null)
                    {
                        panel.SetRow(control, i - 1);
                    }
                }
            }

            var removeStyle = panel.RowCount - 1;

            if (panel.RowStyles.Count > removeStyle)
                panel.RowStyles.RemoveAt(removeStyle);

            panel.RowCount--;

            panel.AutoScroll = false;
            panel.AutoScroll = true;
        }
    }
}

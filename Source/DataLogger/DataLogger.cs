﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static Upatcher;
using static Helpers;
using static LoggerUtils;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.IO.Ports;
using System.Threading.Tasks;
using System.Globalization;
using System.Text.RegularExpressions;
using static UniversalPatcher.PidConfig;

namespace UniversalPatcher
{
    public class DataLogger
    {
        public DataLogger(bool UseVPW)
        {
            Receiver = new MessageReceiver();
            VPWProtocol = UseVPW;
            RealTimeControls = LoadRealTimeControls();
        }
        private CancellationTokenSource logTokenSource = new CancellationTokenSource();
        private CancellationTokenSource logWriterTokenSource = new CancellationTokenSource();
        private CancellationToken logToken;
        private CancellationToken logWriterToken;
        public Task logTask;
        private Task logWriterTask;
        public IPort port;
        public bool Connected = false;
        public List<PidConfig> PidProfile { get; set; }
        private StreamWriter logwriter;
        private string logseparator = ";";
        public Device LogDevice;
        public int ReceivedBytes = 0;
        public string OS;
        public SlotHandler slothandler;
        public MessageReceiver Receiver;

        public bool LogRunning = false;
        public bool AnalyzerRunning = false;
        private  bool AllSlotsRequested = false;
        private  bool passive;
        public int maxPassiveSlotsPerRequest = 50;
        public  bool stopLogLoop;
        public  byte priority = Priority.Physical0;
        private  DateTime lastPresent = DateTime.Now;
        private  DateTime lastElmStop = DateTime.Now;

        //public  Queue<Analyzer.AnalyzerData> analyzerq = new Queue<Analyzer.AnalyzerData>();
        public  Queue<LogData> LogFileQueue = new Queue<LogData>();
        private Queue<QueuedCommand> queuedCommands = new Queue<QueuedCommand>();

        public List<LogData> LogDataBuffer;
        public List<int> TestedPids;

        //Set these values before StartLogging()
        public DateTime LogStartTime;
        public bool writelog;
        public  bool useRawValues;
        public  bool useVPWFilters;
        public  bool reverseSlotNumbers;
        public  byte Responsetype;
        public  int maxSlotsPerRequest = 4;   //How many Slots before asking more
        public  int maxSlotsPerMessage = 4;   //How many Slots in one Slot request message
        public  bool HighPriority = false;
        public bool VPWProtocol = true;
        public ushort CanPcmAddr;
        public byte CanPcmAddrByte1;
        public byte CanPcmAddrByte2;

        public enum QueueCmd
        {
            Getdtc,
            GetVin,
            Custom
        }
        public class QueuedCommand
        {
            public QueueCmd Cmd { get; set; }
            public byte param1 { get; set; }
            public byte param2 { get; set; }
            public OBDMessage CustomMsg { get; set; }
            public string Description { get; set; }
        }

        public enum LoggingDevType
        {
            Elm,
            Obdlink,
            Avt,
            Jet,
            OBDX,
            J2534
        }

        public class LogData
        {
            public LogData(int Size)
            {
                Values = new double[Size];
                CalculatedValues = new double[Size];
            }
            public double[] Values { get; set; }
            public double[] CalculatedValues { get; set; }
            public long TimeStamp { get; set; }
            public ulong DevTimeStamp { get; set; }
        }

        public class ReadValue
        {
            public ReadValue()
            {
                PidValue = double.MinValue;
                FailureCode = 0;
            }
            public string ErrorText
            {
                get
                {
                    if (FailureCode == 0)
                        return "";
                    return PcmResponses[FailureCode];
                }
            }
            public double PidValue { get; set; }
            public int PidNr { get; set; }
            public long TimeStamp { get; set; }
            public ulong DevTimeStamp { get; set; }
            public byte FailureCode { get; set; }
        }

        public class DTCCodeStatus
        {
            public string Module { get; set; }
            public string Code { get; set; }
            public string Description { get; set; }
            public string Status { get; set; }
        }

        public class LogDataEvents
        {
            public class LogDataEvent : EventArgs
            {
                public LogDataEvent(LogData data)
                {
                    this.Data = data;
                }
                public LogData Data { get; internal set; }
            }

            public event EventHandler<LogDataEvent> LogDataAdded;

            protected virtual void OnLogUpdated(LogDataEvent e)
            {
                LogDataAdded?.Invoke(this, e);
            }

            public void Add(LogData data)
            {
                LogDataEvent lde = new LogDataEvent(data);
                OnLogUpdated(lde);
            }

        }

        public Device CreateSerialDevice(string serialPortName, string serialPortDeviceType, bool ftdi)
        {
            try
            {
                if (ftdi)
                    port = new FTDIPort(serialPortName);
                else
                    port = new Rs232Port(serialPortName);

                Device device;
                switch (serialPortDeviceType)
                {
                    case OBDXProDevice.DeviceType:
                        device = new OBDXProDevice(port);
                        break;

                    case AvtDevice.DeviceType:
                        device = new AvtDevice(port);
                        break;

                    case JetDevice.DeviceType:
                        device = new JetDevice(port);
                        break;

                    case ElmDevice.DeviceType:
                        device = new ElmDevice(port);
                        break;

                    default:
                        device = null;
                        break;
                }

                if (device == null)
                {
                    return null;
                }

                return device;
            }
            catch (Exception exception)
            {
                LoggerBold($"Unable to create {serialPortDeviceType} on {serialPortName}.");
                Debug.WriteLine(exception.ToString());
                return null;
            }
        }

        public  bool CreateLog(string path)
        {
            try
            {
                Logger("Writing to logfile: " + path);
                logseparator = AppSettings.LoggerLogSeparator;
                LogFileQueue.Clear();
                if (logwriter != null && logwriter.BaseStream != null)
                {
                    logwriter.Close();
                }
                if (File.Exists(path))
                {                    
                    logwriter = new StreamWriter(path, true);
                }
                else
                {
                    StringBuilder sb = new StringBuilder("Time" + logseparator + "Elapsed time" + logseparator);
                    if (useRawValues)
                    {
                        List<string> pids = new List<string>();
                        for (int c = 0; c < PidProfile.Count; c++)
                        {
                            if (!pids.Contains(PidProfile[c].Address))
                            {
                                pids.Add(PidProfile[c].Address);
                            }
                        }
                        for (int c=0; c < pids.Count; c++)
                        {
                            string s = pids[c];
                            sb.Append(s);
                            if (c<pids.Count-1)
                                 sb.Append(logseparator);
                        }
                    }
                    else
                    {
                        for (int c = 0; c < PidProfile.Count; c++)
                        {
                            sb.Append(PidProfile[c].PidName);
                            if (c < PidProfile.Count - 1)
                                sb.Append(logseparator);
                        }
                    }
                    string header = sb.ToString();
                    logwriter = new StreamWriter(path);
                    logwriter.WriteLine(header);
                }
                return true;
            }
            catch (Exception ex)
            {
                LoggerBold("Create logfile: " + ex.Message);
            }
            return false;
        }

        public void WriteLog(string[] logvalues, string timestamp)
        {
            try
            {
                if (logwriter != null && writelog)
                {
                    string CultureDecim = Thread.CurrentThread.CurrentCulture.NumberFormat.NumberDecimalSeparator;
                    string Decim = AppSettings.LoggerDecimalSeparator;
                    StringBuilder sb = new StringBuilder(timestamp + logseparator + DateTime.Now.Subtract(LogStartTime).ToString() + logseparator);
                    for (int c = 0; c < logvalues.Length; c++)
                    {
                        sb.Append(logvalues[c].Replace(CultureDecim, Decim));
                        if (c < (logvalues.Length - 1))
                            sb.Append(logseparator);
                    }
                    logwriter.WriteLine(sb.ToString());
                }
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, WriteLog line " + line + ": " + ex.Message);
            }
        }


        private void LogWriterLoop()
        {
            Thread.CurrentThread.IsBackground = true;
            //while (!stopLogLoop)
            while (!logWriterToken.IsCancellationRequested)
            {
                while (LogFileQueue.Count == 0)
                {
                    Thread.Sleep(100);
                    if (stopLogLoop)
                    {
                        if (logwriter != null && logwriter.BaseStream != null)
                        {
                            logwriter.Close();
                        }
                        logwriter = null;
                        return;
                    }
                }
                LogData ld;
                lock (LogFileQueue)
                {
                    ld = LogFileQueue.Dequeue();
                }
                ld.CalculatedValues = slothandler.CalculatePidDoubleValues(ld.Values);
                if (useRawValues)
                {
                    string[] ldvalues = new string[ld.Values.Length];
                    for (int l = 0; l < ld.Values.Length; l++)
                        ldvalues[l] = ld.Values[l].ToString();
                    WriteLog(ldvalues, ld.TimeStamp.ToString());

                }
                else
                {
                    string tStamp = new DateTime((long)ld.TimeStamp).ToString(AppSettings.LoggerTimestampFormat);
                    //tStamp += " [" + ld.TimeStamp.ToString() + "]";
                    WriteLog(slothandler.CalculatePidValues(ld.Values), tStamp );
                }
                //Data for Histogram & Graphics:
                LoggerDataEvents.Add(ld);
                LogDataBuffer.Add(ld);
            }
            logwriter.Close();
            logwriter = null;
        }

        public void LoadProfile(string FileName)
        {            
            try
            {
                System.Xml.Serialization.XmlSerializer reader = new System.Xml.Serialization.XmlSerializer(typeof(List<PidConfig>));
                System.IO.StreamReader file = new System.IO.StreamReader(FileName);
                PidProfile = (List<PidConfig>)reader.Deserialize(file);
                file.Close();
                AppSettings.LoggerLastProfile = FileName;
                AppSettings.Save();
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, Datalogger line " + line + ": " + ex.Message);
            }            
        }

        public int LoadProfileFromCsv(string FileName)
        {
            int tStamps = 0;
            try
            {
                StreamReader sr = new StreamReader(FileName);
                string hdrLine = sr.ReadLine();
                sr.Close();
                string[] hdrArray = hdrLine.Split(new string[] { AppSettings.LoggerLogSeparator }, StringSplitOptions.None);
                for (int i = 0; i < hdrArray.Length; i++)
                {
                    if (hdrArray[i].ToLower().Contains("time"))
                    {
                        tStamps++;
                    }
                    else
                    {
                        break;
                    }
                }

                PidProfile = new List<PidConfig>();
                for (int p=tStamps; p< hdrArray.Length;p++)
                {
                    PidConfig pc = new PidConfig();
                    pc.PidName = hdrArray[p];
                    pc.addr = 0xffffff;
                    pc.Math = "X";
                    PidProfile.Add(pc);
                }
                slothandler = new SlotHandler(true);
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, Datalogger line " + line + ": " + ex.Message);
            }
            return tStamps;
        }

        public void SaveProfile(string FileName)
        {
            try
            {
                using (FileStream stream = new FileStream(FileName, FileMode.Create))
                {
                    System.Xml.Serialization.XmlSerializer writer = new System.Xml.Serialization.XmlSerializer(typeof(List<PidConfig>));
                    writer.Serialize(stream, PidProfile);
                    stream.Close();
                }
                AppSettings.LoggerLastProfile = FileName;
                AppSettings.Save();
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, DataLogger line " + line + ": " + ex.Message +", inner exception: " + ex.InnerException);
            }
        }

        public void RemoveUnsupportedPids()
        {
            try
            {
                Logger("Checking pid compatibility... ", false);
                Receiver.StopReceiveLoop();
                if (TestedPids == null)
                {
                    TestedPids = new List<int>();
                }
                for (int p = PidProfile.Count - 1; p >= 0; p--)
                {
                    //if (PidProfile[p].Math.ToUpper().StartsWith("WB"))
                    if (PidProfile[p].addr <= 0 || PidProfile[p].Type == DefineBy.Math)    //Dont test math pids
                    {
                        continue;   
                    }
                    if (TestedPids.Contains(PidProfile[p].addr))
                    {
                        if (PidProfile[p].addr2 < 0)
                        {
                            continue;   //Math, pid2 tested and compatible
                        }
                        else
                        {
                            if (TestedPids.Contains(PidProfile[p].addr2))
                            {
                                continue; //Pid tested and compatible
                            }
                        }
                    }
                    bool compatible = false;
                    Logger(PidProfile[p].addr.ToString("X4") + " ", false);
                    ReadValue rv = datalogger.QuerySinglePidValue(PidProfile[p].addr, datalogger.PidProfile[p].DataType);
                    if (rv.PidValue > double.MinValue && rv.FailureCode == 0)
                    {
                        compatible = true;
                        TestedPids.Add(PidProfile[p].addr);
                        if (PidProfile[p].addr2 > -1)
                        {
                            Logger(PidProfile[p].addr2.ToString("X4") + " ", false);
                            ReadValue rv2 = datalogger.QuerySinglePidValue(PidProfile[p].addr2, datalogger.PidProfile[p].Pid2DataType);
                            if (rv2.PidValue == double.MinValue || rv2.FailureCode != 0)
                            {
                                compatible = false;
                            }
                            else
                            {
                                TestedPids.Add(PidProfile[p].addr2);
                            }
                        }
                    }
                    if (!compatible)
                    {
                        Logger("Removing incompatible pid: " + PidProfile[p].PidName);
                        PidProfile.RemoveAt(p);
                    }
                }
                Logger("Done");
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, datalogger line " + line + ": " + ex.Message);
            }
        }

        public ReadValue QuerySinglePidValue(int addr, PidConfig.ProfileDataType dataType)
        {
            //Receiver.SetReceiverPaused(true);
            ReadValue rv = new ReadValue();
            try
            {
                OBDMessage request = null;
                ushort expectedSrc;
                if (VPWProtocol)
                {
                    expectedSrc = (ushort)(DeviceId.Tool << 8 | DeviceId.Pcm);
                    if (addr > ushort.MaxValue) //RAM
                    {
                        request = new OBDMessage(new byte[] { Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, 0x23, (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr, 0x01 });
                    }
                    else
                    {
                        request = new OBDMessage(new byte[] { Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, 0x22, (byte)(addr >> 8), (byte)addr, 0x01 });
                    }
                }
                else //CAN
                {
                    expectedSrc = CanPcmAddr;
                    if (addr > ushort.MaxValue) //RAM
                    {
                        request = new OBDMessage(new byte[] { 0x00, 0x00, CanPcmAddrByte1, CanPcmAddrByte2, 0x23, (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr, 0x01 });
                    }
                    else
                    {
                        request = new OBDMessage(new byte[] { 0x00, 0x00, CanPcmAddrByte1, CanPcmAddrByte2, 0x22, (byte)(addr >> 8), (byte)addr });
                    }
                }
                LogDevice.ReceiveBufferedMessages();
                LogDevice.ClearMessageQueue();
                //LogDevice.SetTimeout(TimeoutScenario.ReadProperty);
                if (LogDevice.SendMessage(request,1))
                {
                    Application.DoEvents();
                    OBDMessage resp = LogDevice.ReceiveMessage(true);
                    //if (resp.GetBytes()[3] == 0x7f)
                    DateTime startTime = DateTime.Now;
                    int retry = 0;
                    while (true)
                    {
                        //if (resp != null && resp.Length > 3)
                        if (ValidateQueryResponse(resp,expectedSrc))
                        {
                            rv = ParseSinglePidMessage(resp, dataType);
                            if (rv.FailureCode == 0x23)
                            {
                                LogDevice.SendMessage(request, 1);
                            }
                            else if (rv.FailureCode == 0x31)
                            {
                                LoggerBold(Environment.NewLine +  "Pid not supported: " + addr.ToString("X4"));
                                //Receiver.SetReceiverPaused(false);
                                return rv;
                            }
                            else if (rv.FailureCode > 0)
                            {
                                LoggerBold("Pid request failed, pid: " + addr.ToString("X4") + ", Error: " + rv.ErrorText);
                                //Receiver.SetReceiverPaused(false);
                                return rv;
                            }
                            if (rv.PidNr == addr)
                            {
                                Debug.WriteLine("Pid received after " + retry.ToString() + " retries");
                                //Receiver.SetReceiverPaused(false);
                                return rv;
                            }
                            else
                            {
                                Debug.WriteLine("Requested pid: " + addr.ToString("X4") + ", received: " + rv.PidNr.ToString("X4"));
                            }
                        }
                        Application.DoEvents();
                        if (DateTime.Now.Subtract(startTime) > TimeSpan.FromMilliseconds(1000))
                        {
                            LoggerBold("Timeout requesting pid: " + addr.ToString("X4"));
                            //Receiver.SetReceiverPaused(false);
                            return rv;
                        }
                        retry++;
                        resp = LogDevice.ReceiveMessage(true);
                    }
                }
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, Datalogger line " + line + ": " + ex.Message);
            }
            ///Receiver.SetReceiverPaused(false);
            return rv;
        }


        public ReadValue QueryModulePidValue(int addr, ushort module)
        {
            //Receiver.SetReceiverPaused(true);
            ReadValue rv = new ReadValue();
            try
            {
                OBDMessage request = null;
                ushort expectedSrc;
                if (VPWProtocol)
                {
                    expectedSrc = (ushort)(DeviceId.Tool << 8 | module);
                    if (addr > ushort.MaxValue) //RAM
                    {
                        request = new OBDMessage(new byte[] { Priority.Physical0, (byte)module, DeviceId.Tool, 0x23, (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr, 0x01 });
                    }
                    else
                    {
                        request = new OBDMessage(new byte[] { Priority.Physical0,(byte)module, DeviceId.Tool, 0x22, (byte)(addr >> 8), (byte)addr, 0x01 });
                    }
                }
                else //CAN
                {
                    expectedSrc = module;
                    byte moduleByte1 = (byte)(module >> 8);
                    byte moduleByte2 = (byte)(module);
                    if (addr > ushort.MaxValue) //RAM
                    {
                        request = new OBDMessage(new byte[] { 0x00, 0x00, moduleByte1, moduleByte2, 0x23, (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr, 0x01 });
                    }
                    else
                    {
                        request = new OBDMessage(new byte[] { 0x00, 0x00, moduleByte1, moduleByte2, 0x22, (byte)(addr >> 8), (byte)addr });
                    }
                }
                LogDevice.ReceiveBufferedMessages();
                LogDevice.ClearMessageQueue();
                if (LogDevice.SendMessage(request, 1))
                {
                    Application.DoEvents();
                    OBDMessage resp = LogDevice.ReceiveMessage(true);
                    //if (resp.GetBytes()[3] == 0x7f)
                    DateTime startTime = DateTime.Now;
                    int retry = 0;
                    int offset=0;
                    if (!VPWProtocol)
                        offset = 1;
                    while (true)
                    {
                        //if (resp != null && resp.Length > offset + 3)
                        if (ValidateQueryResponse(resp,expectedSrc))
                        {
                            if (resp[offset + 3] == 0x7f)
                            {
                                rv.FailureCode = resp.GetBytes().Last();
                                if (rv.FailureCode == 0x31) //31 = Out of range
                                {
                                    LoggerBold("Pid not supported: " + addr.ToString("X4"));
                                    return rv;
                                }
                                else if (rv.FailureCode == 0x23)
                                {
                                    Debug.WriteLine("Routine not ready, retry");
                                    LogDevice.SendMessage(request, 1);
                                }
                                else
                                {
                                    LoggerBold("Pid request failed, pid: " + addr.ToString("X4") + ", Error: " + rv.ErrorText);
                                    return rv;
                                }
                            }
                            else if (resp[offset + 3] == 0x62)
                            {
                                rv.PidNr = ReadUint16(resp.GetBytes(), (uint)(offset + 4), true);
                            }
                            else if (resp[offset + 3] == 0x63)
                            {
                                byte[] tmp = new byte[4];
                                Array.Copy(resp.GetBytes(), offset + 4, tmp, 2, 2);
                                tmp[1] = 0xFF;
                                rv.PidNr = (int)ReadUint32(tmp, 0, true);
                            }
                            if (rv.PidNr == addr)
                            {
                                Debug.WriteLine("Pid received after " + retry.ToString() + " retries");
                                string val = "";
                                for (int x=offset +6; x < resp.Length; x++)
                                {
                                    val += resp[x].ToString("X2") + " ";
                                }
                                Logger("Pid: " + addr.ToString("X4") + ", value: " + val);
                                return rv;
                            }
                            else
                            {
                                Debug.WriteLine("Requested pid: " + addr.ToString("X4") + ", received: " + rv.PidNr.ToString("X4"));
                            }
                        }
                        Application.DoEvents();
                        retry++;
                        if (DateTime.Now.Subtract(startTime) > TimeSpan.FromMilliseconds(1000))
                        {
                            LoggerBold("Timeout requesting pid: " + addr.ToString("X4"));
                            return rv;
                        }
                        resp = LogDevice.ReceiveMessage(true);
                    }
                }
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, Datalogger line " + line + ": " + ex.Message);
            }
            //Receiver.SetReceiverPaused(false);
            return rv;
        }

        public bool SetBusQuiet()
        {
            try
            {
                if (!VPWProtocol)
                {
                    Debug.WriteLine("No bus quiet for CAN");
                    return true;
                }
                Debug.WriteLine("Set bus quiet");
                byte[] quietMsg = { priority, DeviceId.Broadcast, DeviceId.Tool, Mode.ExitKernel };
                bool m = LogDevice.SendMessage(new OBDMessage(quietMsg), 10);
                if (m)
                {
                    //Debug.WriteLine("OK");
                }
                else
                {
                    Debug.WriteLine("Unable to set bus quiet");
                    Debug.WriteLine("Expected " + string.Join(" ", Array.ConvertAll(quietMsg, b => b.ToString("X2"))));
                    return false;
                }
                //Thread.Sleep(10);
                return true;
            }
            catch (Exception ex)
            {
                LoggerBold("SetBusQuiet: " + ex.Message);
                return false;
            }
        }



        public Response<List<byte>> QueryDevicesOnBus(bool waitanswer)
        {
            //Receiver.SetReceiverPaused(true);
            List<byte> retVal = new List<byte>();
            try
            {
                Debug.WriteLine("Devices on bus?");
                //LogDevice.ClearMessageBuffer();
                LogDevice.ClearMessageQueue();
                //LogDevice.SetTimeout(TimeoutScenario.DataLogging3);
                byte[] queryMsg = { Priority.Physical0High, DeviceId.Broadcast, DeviceId.Tool, 0x20 };
                bool m = LogDevice.SendMessage(new OBDMessage(queryMsg) ,100);
                if (m)
                {
                    //Debug.WriteLine("OK" );
                }
                else
                {
                    Logger("No respond to Query devices message");
                    Debug.WriteLine("Expected " + string.Join(" ", Array.ConvertAll(queryMsg, b => b.ToString("X2"))));
                    //Receiver.SetReceiverPaused(false);
                    return new Response<List<byte>>(ResponseStatus.Error,retVal);
                }
                if (waitanswer)
                {
                    Logger("Waiting for devices", false);
                    for (int wait = 0; wait < 3; wait++)
                    {
                        Thread.Sleep(100);
                        DateTime startTime = DateTime.Now;
                        OBDMessage resp;
                        do
                        {
                            resp = LogDevice.ReceiveMessage(true);
                            if (resp != null && resp.Length > 3 && resp[1] == DeviceId.Tool && resp[3] == 0x60)
                            {
                                byte module = resp[2];
                                if (!retVal.Contains(module))
                                    retVal.Add(module);
                                Debug.WriteLine("Response: " + resp.ToString());
                            }
                            if (DateTime.Now.Subtract(startTime) > TimeSpan.FromMilliseconds(300))
                            {
                                Debug.WriteLine("Timeout waiting for null message");
                                break;
                            }
                        } while (resp != null);
                        Logger(".", false);
                        Application.DoEvents();
                    }
                    Logger(" [Done]");
                }
                //Receiver.SetReceiverPaused(false);
                return new Response<List<byte>>(ResponseStatus.Success, retVal);
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, Datalogger line " + line + ": " + ex.Message);
            }
            //Receiver.SetReceiverPaused(false);
            return new Response<List<byte>>(ResponseStatus.Error, retVal);
        }

        public Response<List<OBDMessage>> QueryModules(int DevCount)
        {
            List<OBDMessage> retVal = new List<OBDMessage>();
            try
            {
                Debug.WriteLine("Modules?");
                LogDevice.ClearMessageQueue();
                //Receiver.SetReceiverPaused(true);
                Logger("Querying modules", false);
                Application.DoEvents();
                OBDMessage resp = null;
                for (byte modId = 0; modId < 0xFF; modId++)
                {
                    if (!Connected)
                    {
                        return new Response<List<OBDMessage>>(ResponseStatus.Error, retVal);
                    }
                    byte[] queryMsg = { Priority.Physical0High, DeviceId.Broadcast, DeviceId.Tool, 0x3c, modId };
                    //LogDevice.SetTimeout(TimeoutScenario.DataLogging1);
                    LogDevice.ReceiveBufferedMessages();
                    LogDevice.SendMessage(new OBDMessage(queryMsg), DevCount + 5);
                    if (modId % 10 == 0)
                    {
                        Logger(".", false);
                        Application.DoEvents();
                    }
                    Thread.Sleep(20);
                    DateTime startTime1 = DateTime.Now;
                    do
                    {
                        resp = LogDevice.ReceiveMessage(false);
                        if (resp != null && resp.Length > 5 && resp[1] == DeviceId.Tool && resp[3] == 0x7c)
                        {
                            retVal.Add(resp);
                            Debug.WriteLine("Response: " + resp.ToString());
                        }
                        Application.DoEvents();
                        if (DateTime.Now.Subtract(startTime1) > TimeSpan.FromMilliseconds(1000))
                        {
                            Debug.WriteLine("Timeout waiting for null message");
                            break;
                        }
                    } while (resp != null);
                }
                Thread.Sleep(100);
                DateTime startTime2 = DateTime.Now;
                do
                {
                    resp = LogDevice.ReceiveMessage(true);
                    if (resp != null && resp.Length > 5 && resp[1] == DeviceId.Tool && resp[3] == 0x7c)
                    {
                        retVal.Add(resp);
                        Debug.WriteLine("Response: " + resp.ToString());
                    }
                    Application.DoEvents();
                    if (DateTime.Now.Subtract(startTime2) > TimeSpan.FromMilliseconds(1000))
                    {
                        Debug.WriteLine("Timeout waiting for null message");
                        break;
                    }
                } while (resp != null);
                Logger(" [Done]");
                //Receiver.SetReceiverPaused(false);
                return new Response<List<OBDMessage>>(ResponseStatus.Success, retVal);
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, Datalogger line " + line + ": " + ex.Message);
                //Receiver.SetReceiverPaused(false);
                return new Response<List<OBDMessage>>(ResponseStatus.Error, retVal);
            }
        }

        public int[] SetCanQueryFilter(ushort module)
        {
            try
            {
                if (module == CanPcmAddr)
                {
                    return null;    //Filter already set for CPM
                }
                CANDevice cd = CANQuery.GetDeviceAddresses(module);
                string filterTxt = "Type:FLOW_CONTROL_FILTER,Name:CANtmpFlowFilter" + Environment.NewLine;
                filterTxt += "Mask: FFFFFFFF,RxStatus: NONE,TxFlags: NONE" + Environment.NewLine;
                filterTxt += "Pattern: 0000" + cd.ResID.ToString("X4") + ",RxStatus: NONE,TxFlags: NONE" + Environment.NewLine;
                filterTxt += "FlowControl: 0000" + cd.RequestID.ToString("X4") + ",RxStatus:NONE,TxFlags:NONE" + Environment.NewLine;
                filterTxt += "Type:PASS_FILTER,Name:CANtmpPassFilter" + Environment.NewLine;
                filterTxt += "Mask: FFFFFFFF,RxStatus: NONE,TxFlags: NONE" + Environment.NewLine;
                filterTxt += "Pattern:0000" + cd.DiagID.ToString("X4") + ",RxStatus: NONE,TxFlags: NONE" + Environment.NewLine;
                Application.DoEvents();
                return LogDevice.SetupFilters(filterTxt, false, false);

            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, Datalogger line " + line + ": " + ex.Message);
                return null;
            }
        }

        public Response<List<OBDMessage>> QueryCANModules(ushort module)
        {
            List<OBDMessage> retVal = new List<OBDMessage>();
            int[] filterIds = SetCanQueryFilter(module);
            Thread.Sleep(100);
            ResponseStatus responseStatus = ResponseStatus.Success;
            try
            {
                Debug.WriteLine("Modules?");
                //LogDevice.ClearMessageBuffer();
                LogDevice.ClearMessageQueue();
                Receiver.SetReceiverPaused(true);
                Logger("Querying modules", false);
                Application.DoEvents();
                OBDMessage resp = null;
                for (byte modId = 0; modId < 0xFF; modId++)
                {
                    byte[] queryMsg = { 0x00, 0x00, (byte)(module >> 8), (byte)module, 0x1a, modId };
                    //LogDevice.SetTimeout(TimeoutScenario.DataLogging1);
                    LogDevice.SendMessage(new OBDMessage(queryMsg), 5);
                    if (modId % 10 == 0)
                    {
                        Logger(".", false);
                        Application.DoEvents();
                    }
                    Thread.Sleep(50);
                    DateTime startTime = DateTime.Now;
                    do
                    {
                        resp = LogDevice.ReceiveMessage(false);
                        if (resp != null && resp.Length > 5 && resp[4] == 0x5A)
                        {
                            retVal.Add(resp);
                            Debug.WriteLine("Response: " + resp.ToString());
                        }
                        if (DateTime.Now.Subtract(startTime) > TimeSpan.FromMilliseconds(300))
                        {
                            Debug.WriteLine("Timeout waiting for null message");
                            break;
                        }
                    } while (resp != null);
                }

                Thread.Sleep(100);
                DateTime startTime2 = DateTime.Now;
                do
                {
                    resp = LogDevice.ReceiveMessage(true);
                    if (resp != null && resp.Length > 5 && resp[4] == 0x5A)
                    {
                        retVal.Add(resp);
                        Debug.WriteLine("Response: " + resp.ToString());
                    }
                    if (DateTime.Now.Subtract(startTime2) > TimeSpan.FromMilliseconds(300))
                    {
                        Debug.WriteLine("Timeout waiting for null message");
                        break;
                    }
                } while (resp != null) ;
                Logger(" [Done]");
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, Datalogger line " + line + ": " + ex.Message);
                responseStatus = ResponseStatus.Error;
            }
            finally
            {
                if (filterIds != null)
                {
                    LogDevice.RemoveFilters(filterIds);
                }
                Receiver.SetReceiverPaused(false);
            }
            return new Response<List<OBDMessage>>(responseStatus, retVal);
        }

        public Response<List<OBDMessage>> QueryFreezeFrames(ushort module)
        {
            List<OBDMessage> retVal = new List<OBDMessage>();
            try
            {
                Debug.WriteLine("Freeze frames?");
                //LogDevice.ClearMessageBuffer();
                LogDevice.ClearMessageQueue();
                Receiver.SetReceiverPaused(true);
                Logger("Querying freeze frames", false);
                //LogDevice.SetTimeout(TimeoutScenario.DataLogging1);
                //LogDevice.SetReadTimeout(500);
                Application.DoEvents();
                OBDMessage resp = null;
                byte[] queryMsg;
                ushort expectedSrc;
                int offset;
                if (VPWProtocol)
                {
                    expectedSrc = (ushort)(DeviceId.Tool<<8 | (byte)module);
                    offset = 0;
                }
                else
                {
                    expectedSrc = module;
                    offset = 1;
                }
                for (byte x = 0; x < 6; x++)
                {

                    if (VPWProtocol)
                    {
                        queryMsg = new byte[] { Priority.Physical0High, (byte)module, DeviceId.Tool, 0x12, x, 0xFF, 0xFF };
                        offset = 0;
                    }
                    else
                    {
                        queryMsg = new byte[] { 0x00,0x00,(byte)(module >>8),(byte)module, 0x12, x, 0xFF, 0xFF };
                        offset = 1;
                    }
                    LogDevice.SendMessage(new OBDMessage(queryMsg), 500);
                    Logger(".", false);
                    Application.DoEvents();
                    Thread.Sleep(1000);
                    //if (LogDevice.LogDeviceType == LoggingDevType.J2534)
                    {
                        //LogDevice.SetReadTimeout(1000);
                        DateTime startTime1 = DateTime.Now;
                        do
                        {
                            resp = LogDevice.ReceiveMessage(false);
                            //if (resp != null && resp.Length > 6 && resp[2] == DeviceId.Pcm && resp[3] == 0x52)
                            if (ValidateQueryResponse(resp, expectedSrc) && resp[offset + 3] == 0x52)
                            {
                                retVal.Add(resp);
                                Debug.WriteLine("Response: " + resp.ToString());
                                Application.DoEvents();
                            }
                            if (DateTime.Now.Subtract(startTime1) > TimeSpan.FromMilliseconds(300))
                            {
                                Debug.WriteLine("Timeout waiting for null message");
                                break;
                            }
                        } while (resp != null);
                        //LogDevice.SetReadTimeout(AppSettings.TimeoutJConsoleReceive);
                    }
                }
                Logger(" [Done]");
                Thread.Sleep(100);
                Logger("Waiting answer...", false);
                DateTime startTime2 = DateTime.Now;
                do
                {
                    resp = LogDevice.ReceiveMessage(true);
                    //if (resp != null && resp.Length > 6 && resp[2] == DeviceId.Pcm && resp[3] == 0x52)
                    if (ValidateQueryResponse(resp, expectedSrc) && resp[offset + 3] == 0x52)
                    {
                        retVal.Add(resp);
                        Debug.WriteLine("Response: " + resp.ToString());
                    }
                    Application.DoEvents();
                    if (DateTime.Now.Subtract(startTime2) > TimeSpan.FromMilliseconds(300))
                    {
                        Debug.WriteLine("Timeout waiting for null message");
                        break;
                    }
                } while (resp != null);
                Logger(" [Done]");
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, Datalogger line " + line + ": " + ex.Message);
                Receiver.SetReceiverPaused(false);
                return new Response<List<OBDMessage>>(ResponseStatus.Error, retVal);
            }
            Receiver.SetReceiverPaused(false);
            return new Response<List<OBDMessage>>(ResponseStatus.Success, retVal);
        }

        public void ClearTroubleCodes(ushort module)
        {
            try
            {
                Receiver.SetReceiverPaused(true);
                string moduleStr = module.ToString("X2");
                if (analyzer.PhysAddresses.ContainsKey((byte)module))
                    moduleStr = analyzer.PhysAddresses[(byte)module];
                Logger("Clearing codes of: " + moduleStr);
                //OBDMessage msg = new OBDMessage(new byte[] { Priority.Functional0, 0x6A, DeviceId.Tool, Mode.ClearDiagnosticTroubleCodes });
                OBDMessage msg;
                if (VPWProtocol)
                    msg = new OBDMessage(new byte[] { Priority.Physical0, (byte)module, DeviceId.Tool, 0x14 });
                else
                    msg = new OBDMessage(new byte[] { 0x00, 0x00, (byte)(module >> 8), (byte)module, 0x04 });
                LogDevice.SendMessage(msg, 1);
                OBDMessage resp = LogDevice.ReceiveMessage(true);
                if (resp != null)
                    Debug.WriteLine("Clear DTC response: " + resp.ToString());
                Logger("Done");
            }
            catch (Exception ex)
            {
                LoggerBold("ClearTroubleCodes:" + ex.Message);
            }
            Receiver.SetReceiverPaused(false);
        }


        public  bool SetMode1()
        {
            try
            {
                Debug.WriteLine("Set mode to 1");
                byte[] Msg = { priority, DeviceId.Pcm, DeviceId.Tool, 0x01, 0x01 };
                bool m = LogDevice.SendMessage(new OBDMessage(Msg),1);
                if (!m)
                {
                    Logger("Unable to set mode 1");
                    Debug.WriteLine("Expected " + string.Join(" ", Array.ConvertAll(Msg, b => b.ToString("X2"))));
                    return false;
                }
                while (LogDevice.ReceivedMessageCount > 0)
                {
                    OBDMessage resp = LogDevice.ReceiveMessage(true);
                    Debug.WriteLine("Response: " + resp.ToString());
                }
                Thread.Sleep(50);
                return true;
            }
            catch (Exception ex)
            {
                LoggerBold(ex.Message);
                return false;
            }
        }


        /*
                public  bool InitalizeDevice(string comport, string devtype, bool ftdi, int BaudRate)
                {
                    try
                    {                
                        PcmDevice = CreateSerialDevice(comport, devtype, ftdi);
                        if (PcmDevice.Initialize(BaudRate))
                        {
                            return true;
                        }
                        Thread.Sleep(50);
                    }
                    catch (Exception ex)
                    {
                        LoggerBold(ex.Message);
                    }
                    if (PcmDevice != null)
                    {
                        PcmDevice.Dispose();
                    }
                    return false;
                }

        */
        /*        public  bool InitalizeJDevice(string devtype)
                {
                    try
                    {
                        J2534DotNet.J2534Device dev = J2534DeviceFinder.FindInstalledJ2534DLLs().Where(x => x.Name == devtype).FirstOrDefault();
                        PcmDevice = new J2534Device(dev);
                        if (PcmDevice.Initialize(0))
                            return true;
                    }
                    catch (Exception ex)
                    {
                        LoggerBold(ex.Message);
                    }
                    return false;
                }
        */
        public void StopLogging()
        {
            logTokenSource.Cancel();
            stopLogLoop = true;
            Application.DoEvents();
            if (passive)
            {
                if (LogDevice.LogDeviceType != LoggingDevType.J2534 )
                {
                    //DateTime startWait = DateTime.Now;
                    //while (DateTime.Now.Subtract(lastPresent).TotalMilliseconds< 5000)
                    Debug.WriteLine("Waiting for log loop to end");
                    while (LogRunning)
                    {
                        Thread.Sleep(100);
                        Application.DoEvents();
                    }
                    Debug.WriteLine("Logging loop finished");
                }
            }
        }

        public bool StartLogging(bool UseVPW, bool ReConnect)
        {
            try
            {
                VPWProtocol = UseVPW;
                ReceivedBytes = 0;

                if (LogDevice == null)
                {
                    LoggerBold("Connection failed");
                    return false;
                }
                if (Receiver.ReceiveLoopRunning) // && LogDevice.LogDeviceType != LoggingDevType.J2534)
                {
                    Receiver.StopReceiveLoop();
                }

                LogDevice.SetTimeout(TimeoutScenario.DataLogging1);

                if (AnalyzerRunning == false && UseVPW == true)
                {
                    if (!LogDevice.SetLoggingFilter())
                        return false;
                }
                Logger("Pid setup...");
                Application.DoEvents();
                if (!ReConnect)
                {
                    slothandler = new SlotHandler(UseVPW);
                    bool resp = slothandler.CreatePidSetupMessages();

                    if (!resp)
                    {
                        return false;
                    }
                }
                if (!RequestFirstSlots())
                {
                    return false;
                }
                stopLogLoop = false;
                logTokenSource = new CancellationTokenSource();
                logToken = logTokenSource.Token;
                LogDataBuffer = new List<LogData>();
                logTask = Task.Factory.StartNew(() => DataLoggingLoop(), logToken);
                logWriterTokenSource = new CancellationTokenSource();
                logWriterToken = logWriterTokenSource.Token;
                logWriterTask = Task.Factory.StartNew(() => LogWriterLoop(), logWriterToken);
                return true;
            }
            catch (Exception ex)
            {
                logwriter = null;
                LogDevice.Dispose();
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, StartLogging line " + line + ": " + ex.Message);
                return false;
            }
        }

        private bool RequestFirstSlots()
        {
            AllSlotsRequested = false;
            if (VPWProtocol)
            {
                if (Responsetype == ResponseType.SendOnce)
                {
                    passive = false;
                    maxSlotsPerMessage = 6;
                    maxSlotsPerRequest = 6;
                }
                else
                {
                    passive = true;
                    maxSlotsPerMessage = 4;
                    maxSlotsPerRequest = 4;
                }
                if (Responsetype == ResponseType.SendFast)
                {
                    Responsetype = ResponseType.SendMedium2;    //VPW Can't start with 0x24
                }
                if (QueryDevicesOnBus(false).Status != ResponseStatus.Success)
                    return false;
                if (!SetBusQuiet())
                    return false;
                if (!SetBusQuiet())
                    return false;
            }
            else
            {
                if (Responsetype == ResponseType.SendOnce)
                {
                    maxSlotsPerRequest = 5;
                    maxSlotsPerMessage = 5;
                    passive = false;
                }
                else
                {
                    maxSlotsPerRequest = 16;
                    maxSlotsPerMessage = 16;
                    passive = true;
                }
            }

            Logger("Requesting pids...");
            Application.DoEvents();

            if (passive)
            {
                lastPresent = DateTime.Now;
                //elmStopTreshold = 1000;
                LogDevice.SetTimeout(AppSettings.TimeoutLoggingPassive);
                LogDevice.ClearLogQueue();
                if (!RequestPassiveModeSlots())
                {
                    LoggerBold("Error requesting Slots");
                    return false;
                }
            }
            else
            {
                //elmStopTreshold = 50;
                if (LogDevice.LogDeviceType == LoggingDevType.Obdlink)
                {
                    LogDevice.SetTimeout(AppSettings.TimeoutLoggingActiveObdlink);
                }
                else
                {
                    LogDevice.SetTimeout(AppSettings.TimeoutLoggingActive);
                }
                LogDevice.ClearLogQueue();
                RequestNextSlots();
            }
            return true;
        }

        public bool RequestNextSlots()
        {
            OBDMessage msg;
            byte[] rq = slothandler.CreateNextSlotRequestMessage();
            msg = new OBDMessage(rq);
            Debug.WriteLine("Requesting Slots: " + msg.ToString());
            bool resp =  LogDevice.SendMessage(msg, -maxSlotsPerRequest);
            Thread.Sleep(100);
            Application.DoEvents();
            return resp;
        }


        public  string QueryPcmOS()
        {
            try
            {
                Receiver.SetReceiverPaused(true);
                Debug.WriteLine("OS?");
                //LogDevice.ClearMessageBuffer();
                LogDevice.ClearMessageQueue();
                byte[] queryMsg;
                uint pos;
                if (VPWProtocol)
                {
                    queryMsg = new byte[] { Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.ReadBlock, 0x0A }; //6C 10 F0 3C 0A
                    pos = 5;
                }
                else
                {
                    queryMsg = new byte[] { 0x00, 0x00, CanPcmAddrByte1, CanPcmAddrByte2, 0x1A, 0xC1 };
                    pos = 6;
                }
                bool m = LogDevice.SendMessage(new OBDMessage(queryMsg), 1);
                if (!m)
                {
                    //Logger("No respond to OS Query message");
                    Debug.WriteLine("Expected " + string.Join(" ", Array.ConvertAll(queryMsg, b => b.ToString("X2"))));
                    return null;
                }
                Thread.Sleep(30);
                OBDMessage resp = LogDevice.ReceiveMessage(true);
                DateTime startTime = DateTime.Now;
                while (true)
                {
                    if (resp != null && resp.Length > 5)
                    {
                        if (VPWProtocol)
                        {
                            if (resp[1] == DeviceId.Tool && resp[2] == DeviceId.Pcm)
                            {
                                break;
                            }
                        }
                        else
                        {
                            if (resp[2] == CanPcmAddrByte1 && resp[3] == CanPcmAddrByte2)
                            {
                                break;
                            }
                        }
                    }
                    if (DateTime.Now.Subtract(startTime) > TimeSpan.FromMilliseconds(1000))
                    {
                        LoggerBold("Timeout requesting OS");
                        return null;
                    }
                    resp = LogDevice.ReceiveMessage(true);
                }
                uint os = ReadUint32(resp.GetBytes(), pos, true);
                Debug.WriteLine("Response: " + resp.ToString());
                OS = os.ToString();
                Receiver.SetReceiverPaused(false);
                return os.ToString();
            }
            catch(Exception ex)
            {
                Debug.WriteLine("QyeryPcmOS: " + ex.Message);
                Receiver.SetReceiverPaused(false);
                return null;
            }
        }

        public void QueryVIN()
        {
            string vin = "";
            try
            {
                Debug.WriteLine("VIN?");
                Receiver.SetReceiverPaused(true);
                LogDevice.ClearMessageQueue();
                //LogDevice.ClearMessageBuffer();
                byte[] vinbytes = new byte[3*6];
                if (VPWProtocol)
                {
                    for (byte v = 0; v < 3; v++)
                    {
                        LogDevice.ReceiveBufferedMessages();
                        byte[] queryMsg = { Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.ReadBlock, (byte)(v + 1) };
                        bool m = LogDevice.SendMessage(new OBDMessage(queryMsg), 1);
                        if (!m)
                        {
                            Logger("No response to VIN Query message");
                            Debug.WriteLine("Expected " + string.Join(" ", Array.ConvertAll(queryMsg, b => b.ToString("X2"))));
                            Receiver.SetReceiverPaused(false);
                            return;
                        }
                        //Thread.Sleep(100);
                        Application.DoEvents();
                        OBDMessage resp;
                        DateTime startTime = DateTime.Now;
                        while (true)
                        {
                            resp = LogDevice.ReceiveMessage(true);
                            if (resp == null)
                                Debug.WriteLine("Null msg");
                            else
                                Debug.WriteLine("Msg: " + resp.ToString());
                            if (resp != null && resp.Length > 10 && resp[1] == DeviceId.Tool && resp[2] == DeviceId.Pcm && resp[3] == 0x7C && resp[4] == v + 1)
                            {
                                Array.Copy(resp.GetBytes(), 5, vinbytes, v * 6, 6);
                                Debug.WriteLine("Response: " + resp.ToString());
                                break;
                            }
                            if (DateTime.Now.Subtract(startTime) > TimeSpan.FromMilliseconds(1000))
                            {
                                LoggerBold("Timeout receiving VIN code");
                                Receiver.SetReceiverPaused(false);
                                return;
                            }
                        } 
                    }
                }
                else //CAN
                {
                    byte[] queryMsg = { 0x00, 0x00, CanPcmAddrByte1, CanPcmAddrByte2, 0x1A, 0x90 };
                    bool m = LogDevice.SendMessage(new OBDMessage(queryMsg), 1);
                    if (!m)
                    {
                        Logger("No response to VIN Query message");
                        Debug.WriteLine("Expected " + string.Join(" ", Array.ConvertAll(queryMsg, b => b.ToString("X2"))));
                        Receiver.SetReceiverPaused(false);
                        return;
                    }
                    Thread.Sleep(100);
                    OBDMessage resp = LogDevice.ReceiveMessage(true);
                    DateTime startTime = DateTime.Now;
                    CANDevice cd = CANQuery.GetDeviceAddresses(CanPcmAddr);
                    while (true)
                    {
                        if (resp != null && resp.Length >= 23 && ValidateQueryResponse(resp, CanPcmAddr)) // resp[2] == CanPcmAddrByte1 && resp[3] == 0xE8)
                        {                            
                            Array.Copy(resp.GetBytes(), 6, vinbytes, 0, 17);
                            break;
                        }
                        if (DateTime.Now.Subtract(startTime) > TimeSpan.FromMilliseconds(1000))
                        {
                            LoggerBold("Timeout requesting OS");
                            return;
                        }
                        resp = LogDevice.ReceiveMessage(true);
                    }
                }
                Receiver.SetReceiverPaused(false);
                vin = Encoding.ASCII.GetString(vinbytes, 1, 17);
                vin = Regex.Replace(vin, "[^a-zA-Z0-9]", "?");
                Logger("VIN Code:" + vin);

            }
            catch (Exception ex)
            {
                Debug.WriteLine("QueryVIN: " + ex.Message);
            }
            Receiver.SetReceiverPaused(false);
        }

        public DTCCodeStatus DecodeDTCstatus(byte[] msg)
        {
            DTCCodeStatus dcs = new DTCCodeStatus();
            try
            {
                ushort dtc = ReadUint16(msg, 4, true);
                byte stat = msg[6];
                if (stat > 0)
                {
                    string code = DtcSearch.DecodeDTC(dtc.ToString("X4"));
                    dcs.Code = code;
                    if (VPWProtocol)
                    {
                        byte module = msg[2];
                        dcs.Module = module.ToString("X2");
                        if (analyzer.PhysAddresses.ContainsKey(module))
                            dcs.Module = analyzer.PhysAddresses[module];
                    }
                    else
                    {
                        ushort module = (ushort)(msg[3] << 8 | msg[4]);
                        for(int m=0;m< CanModules.Count; m++)
                        {
                            if (CanModules[m].RequestID == module || CanModules[m].DiagID == module || CanModules[m].ResID == module)
                            {
                                dcs.Module = CanModules[m].ModuleName;
                                break;
                            }
                        }
                    }
                    OBD2Code descr = OBD2Codes.Where(x => x.Code == code).FirstOrDefault();
                    if (descr != null)
                        dcs.Description = descr.Description;
                    dcs.Status = "";
                    if (stat != 0xFF)
                    {
                        if ((stat & 0x80) == 0x80)
                        {
                            dcs.Status += "MIL ILLUMINATED,";
                        }
                        if ((stat & 0x40) == 0x80)
                        {
                            dcs.Status += "MIL PENDING,";
                        }
                        if ((stat & 0x20) == 0x80)
                        {
                            dcs.Status += "MIL PREVIOUSLY ILLUMINATED -OLD CODE,";
                        }
                        if ((stat & 0x10) == 0x80)
                        {
                            dcs.Status += "STORED TROUBLE CODE (FREEZE FRAME DATA AVAILABLE),";
                        }
                        if ((stat & 0x08) == 0x80)
                        {
                            dcs.Status += "GM SPECIFIC STATUS 1,";
                        }
                        if ((stat & 0x80) == 0x04)
                        {
                            dcs.Status += "GM SPECIFIC STATUS 0,";
                        }
                        if ((stat & 0x80) == 0x02)
                        {
                            dcs.Status += "CURRENT DTC CODE,";
                        }
                        if ((stat & 0x80) == 0x01)
                        {
                            dcs.Status += "IMMATURE DTC CODE,";
                        }
                        dcs.Status = dcs.Status.Trim(',');
                    }
                }
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                Debug.WriteLine("Error, DecodeDTCstatus line " + line + ": " + ex.Message);
            }

            return dcs;
        }

        public void QueueCustomCmd(OBDMessage Msg, string Description)
        {
            Logger("Adding message to queue");
            QueuedCommand command = new QueuedCommand();
            command.Cmd = QueueCmd.Custom;
            command.CustomMsg = Msg;
            command.Description = Description;
            lock (queuedCommands)
            {
                queuedCommands.Enqueue(command);
            }
        }

        public void QueueDtcRequest(ushort module, byte mode)
        {
            Logger("Adding DTC request to queue");
            QueuedCommand command = new QueuedCommand();
            command.Cmd = QueueCmd.Getdtc;
            command.param1 = (byte)module;
            command.param2 = mode;
            lock(queuedCommands)
            {
                queuedCommands.Enqueue(command);
            }
        }
        public void QueueVINRequest()
        {
            Logger("Adding VIN request to queue");
            QueuedCommand command = new QueuedCommand();
            command.Cmd = QueueCmd.GetVin;
            lock (queuedCommands)
            {
                queuedCommands.Enqueue(command);
            }
        }

        public bool RequestDTCCodes(ushort module, byte mode)
        {
            try
            {
                string moduleStr = module.ToString("X2");
                OBDMessage msg;
                if (module == DeviceId.Broadcast)
                {
                    moduleStr = "Broadcast";
                }
                if (VPWProtocol)
                {
                    msg = new OBDMessage(new byte[] { Priority.Physical0, (byte)module, DeviceId.Tool, 0x19, mode, 0xFF, 0x00 });
                    if (analyzer.PhysAddresses.ContainsKey((byte)module))
                        moduleStr = analyzer.PhysAddresses[(byte)module];
                }
                else
                {
                    msg = new OBDMessage(new byte[] { 0x00,0x00,(byte)(module >>8), (byte)(module), mode, 0x81, mode });
                }
                Logger("Requesting DTC codes for " + moduleStr);
                Receiver.SetReceiverPaused(true);
                bool m = LogDevice.SendMessage(msg, -500);
                if (!m)
                {
                    Debug.WriteLine("Error sending request, continue anyway");
                    //return false;
                }
                Thread.Sleep(100);
                Debug.WriteLine("Receiving DTC codes...");
                //byte[] endframe = new byte[] { Priority.Physical0, DeviceId.Tool, module, 0x59, 0x00, 0x00, 0xFF };
                DateTime startTime = DateTime.Now;
                OBDMessage resp;
                do
                {

                    resp = LogDevice.ReceiveMessage(true);
                    if (resp != null)
                    {
                        Debug.WriteLine("DTC received message: " + resp.ToString());
                    }
                    if (DateTime.Now.Subtract(startTime) > TimeSpan.FromMilliseconds(1000))
                    {
                        Debug.WriteLine("Timeout waiting for null message");
                        break;
                    }
                } while (resp != null);
                Logger("Done");
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                Debug.WriteLine("Error, RequestDTCCodes line " + line + ": " + ex.Message);
                return false;
            }
            Receiver.SetReceiverPaused(false);
            return true;
        }


        public  bool SendTesterPresent(bool force)
        {
            try
            {
                if (force || DateTime.Now.Subtract(lastPresent) >= TimeSpan.FromMilliseconds(AppSettings.LoggerTesterPresentInterval)) //4500
                {
                    Debug.WriteLine("Seconds since last testerpresent: " + DateTime.Now.Subtract(lastPresent));
                    {
                        byte[] presentMsg;
                        if (VPWProtocol)
                            presentMsg = new byte[]{ priority, DeviceId.Broadcast, DeviceId.Tool, 0x3F };
                        else
                            presentMsg = new byte[] { 0x00, 0x00, CanPcmAddrByte1, CanPcmAddrByte2, 0x3e };
                        LogDevice.SendMessage(new OBDMessage(presentMsg), -maxSlotsPerRequest);
                        lastPresent = DateTime.Now;
                        Debug.WriteLine("Sent Tester present, force: " + force);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerBold(ex.Message);
                return false;
            }
            return true;
        }

        public ReadValue ParseSinglePidMessage(OBDMessage msg, PidConfig.ProfileDataType datatype)
        {
            ReadValue retVal = new ReadValue();
            try
            {
                int offset = 0;
                if (!VPWProtocol)
                {
                    offset = 1; //CAN protocol have 1 more header byte
                }
                if (msg.Length > offset + 5)
                {
                    Debug.WriteLine("Parsing message: " + msg.ToString());
                    int pos;
                    ReadValue rv = new ReadValue();
                    if (msg[offset + 3] == 0x7f) 
                    {
                        if (msg.GetBytes().Last() == 0x31) //31 = Out of range
                        {
                            Debug.WriteLine("Unsupported pid");
                        }
                        else if (msg.GetBytes().Last() == 0x23) //23 = Routine not ready
                        {
                            Debug.WriteLine("Routine not ready, retry");
                        }
                        rv.FailureCode = msg.GetBytes().Last();
                        return rv;
                    }
                    else if (msg[offset + 3] == 0x62)
                    {
                        rv.PidNr = ReadUint16(msg.GetBytes(), (uint)(offset + 4), true);
                    }
                    else if (msg[offset + 3] == 0x63)
                    {
                        byte[] tmp = new byte[4];
                        Array.Copy(msg.GetBytes(), offset + 4, tmp, 2, 2);
                        tmp[1] = 0xFF;
                        rv.PidNr = (int)ReadUint32(tmp, 0, true);
                    }
                    pos = offset + 6;
                    double val = 0;
                    switch (datatype)
                    {
                        case ProfileDataType.UBYTE:
                            val = (byte)msg[pos];
                            break;
                        case ProfileDataType.SBYTE:
                            val = (sbyte)msg[pos];
                            break;
                        case ProfileDataType.UWORD:
                            if (msg.Length <= pos + 1)
                            {
                                rv.FailureCode = 0x22;
                            }
                            else
                            {
                                val = ReadUint16(msg.GetBytes(), (uint)pos, true);
                            }
                            break;
                        case ProfileDataType.SWORD:
                            if (msg.Length <= pos + 1)
                            {
                                rv.FailureCode = 0x22;
                            }
                            else
                            {
                                val = ReadInt16(msg.GetBytes(), (uint)pos, true);
                            }
                            break;
                    }
                    rv.PidValue = val;
                    Debug.WriteLine("Pid: " + rv.PidNr.ToString("X4") + ", Value: " + val);
                    return rv;
                }
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, ParseSinglePidMessage line " + line + ": " + ex.Message);
            }
            return new ReadValue();
        }

        
        private bool RequestPassiveModeSlots()
        {
            lastPresent = DateTime.Now;
            if (!RequestNextSlots())
                return false;
            if (VPWProtocol)
            {
                if (Responsetype == ResponseType.SendFast)
                {
                    Responsetype = ResponseType.SendMedium2;
                }
                else if (Responsetype == ResponseType.SendMedium2)
                {
                    Responsetype = ResponseType.SendFast;
                }
                if (LogDevice.LogDeviceType != LoggingDevType.Elm && LogDevice.LogDeviceType != LoggingDevType.Obdlink) //If Elm device, need to wait for prompt
                {
                    Thread.Sleep(100);
                    if (!RequestNextSlots())
                        return false;
                }
            }
            return true;
        }

        public void ELMPromptReceived()
        {
            Debug.WriteLine("Elm prompt received");

            if (stopLogLoop)
            {
                SetBusQuiet();
                return;
            }

            SendQueuedCommand();

            if (passive)
            {
                //if (DateTime.Now.Subtract(lastPresent) < TimeSpan.FromMilliseconds(4500) &&  DateTime.Now.Subtract(lastElmPrompt) < TimeSpan.FromSeconds(1))
                  //  return;

                if (!AllSlotsRequested)
                {
                    //LogDevice.SetTimeout(TimeoutScenario.DataLogging4);
                    if (!RequestPassiveModeSlots())
                    {
                        LoggerBold("Error requesting Slots");
                        StopLogging();
                        return;
                    }
                    AllSlotsRequested = true;
                }
                else
                {
                    SendTesterPresent(true);
                }
            }
            else
            {
                RequestNextSlots();
            }

        }

        public void StopElmReceive()
        {
            try
            {
                if (LogDevice.LogDeviceType == LoggingDevType.Elm || LogDevice.LogDeviceType != LoggingDevType.Obdlink)
                {
                    Debug.WriteLine("Time (ms) since last elmStop: " + DateTime.Now.Subtract(lastElmStop).TotalMilliseconds);
                    //if (force || DateTime.Now.Subtract(lastPresent) >= TimeSpan.FromMilliseconds(4500) || DateTime.Now.Subtract(lastElmStop) >= TimeSpan.FromMilliseconds(elmStopTreshold))
                    {
                        //Stop current receive
                        Debug.WriteLine("Stop elm receive");
                        port.Send(Encoding.ASCII.GetBytes("X \r"));
/*                        if (LogDevice.LogDeviceType == LoggingDevType.Elm)
                        {
                            Thread.Sleep(50);
                            byte[] queryMsg = { Priority.Physical0, 0x3, DeviceId.Tool, 0x3f };
                            LogDevice.SendMessage(new OBDMessage(queryMsg), 1);
                        }
                        Thread.Sleep(200);
*/                        //Thread.Sleep(50);
                        OBDMessage msg = LogDevice.ReceiveMessage(true);
                        while (msg != null && !msg.ElmPrompt)
                        {
                            Debug.WriteLine("Elm stop received msg: " + msg.ToString() + ", ElmPrompt: " + msg.ElmPrompt);
                            msg = LogDevice.ReceiveMessage(true);
                        }
                        lastElmStop = DateTime.Now;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("StopElmReceive: " + ex.Message);
            }
        }

        private  bool ValidateLogMessage(OBDMessage oMsg, ref int SlotCount)
        {
            byte lastSlot = slothandler.Slots.Last().Id;
            int bytePos;
            if (VPWProtocol)
            {
                if (!Utility.CompareArraysPart(oMsg.GetBytes(), new byte[] { priority, DeviceId.Tool, DeviceId.Pcm }))
                {
                    Debug.WriteLine("Unknown msg");
                    return false;
                }
                if (oMsg.Length < 11)
                {
                    Debug.WriteLine("Short msg");
                    return false;
                }
                byte slot = oMsg[4];
                if ((reverseSlotNumbers && slot < lastSlot) || (!reverseSlotNumbers && slot > lastSlot))    //It's not slot. Maybe 2A ?
                {
                    return false;
                }
                bytePos = 3;
            }
            else
            {
                if (oMsg.Length < 8)
                {
                    Debug.WriteLine("Short msg");
                    return false;
                }
                CANDevice cd = CANQuery.GetDeviceAddresses(CanPcmAddr);
                ushort msgSrc = (ushort)(oMsg[2] << 8 | oMsg[3]);
                if (oMsg[0] != 0x00 || oMsg[1] != 0x00 || msgSrc != cd.DiagID) // || oMsg[3] != 0xE8)
                {
                    Debug.WriteLine("Unknown msg");
                    return false;
                }
                bytePos = 4;
            }


            byte PcmResponse = oMsg[bytePos];
            if (PcmResponse == 0x7F)
            {
                Debug.WriteLine(oMsg.ToString() + ": " + PcmResponses[oMsg.GetBytes().Last()]);
                SlotCount++;
                return false;
            }

            return true;
        }

        public bool ValidateQueryResponse(OBDMessage oMsg, ushort ExpectedSrc)
        {
            if (oMsg == null)
            {
                return false;
            }
            Debug.WriteLine("Validating message: " + oMsg.ToString());
            if (VPWProtocol)
            {
                if (oMsg.Length < 3)
                {
                    Debug.WriteLine("Short message");
                    return false;
                }
                //priority, DeviceId.Tool, DeviceId.Pcm
                byte eSrc = (byte)(ExpectedSrc >> 8);
                byte eDst = (byte)(ExpectedSrc & 0xFF);
                if (eSrc != oMsg[1])
                {
                    Debug.WriteLine("ValidateQueryResponse: wrong source: " + oMsg[1].ToString("X2"));
                    return false;
                }
                if (eDst != oMsg[2])
                {
                    Debug.WriteLine("ValidateQueryResponse: wrong destination: " + oMsg[2].ToString("X2"));
                    return false;
                }
            }
            else
            {
                if (oMsg.Length < 5)
                {
                    Debug.WriteLine("Short msg");
                    return false;
                }
                if (oMsg[0] != 0 || oMsg[1] != 0)
                {
                    Debug.WriteLine("Unknown msg");
                    return false;
                }
                CANDevice cd = CANQuery.GetDeviceAddresses(ExpectedSrc);
                ushort msgSrc = (ushort)(oMsg[2] << 8 | oMsg[3]);
                if (msgSrc != cd.DiagID && msgSrc != cd.RequestID && msgSrc != cd.ResID) 
                {
                    Debug.WriteLine("Unknown msg");
                    return false;
                }
            }

            return true;
        }
        public void LoadLogFile(string LogFile)
        {
            try
            {
                Logger("Loading file: " + LogFile);
                LogDataBuffer = new List<LogData>();
                DateTime startTime = DateTime.MinValue;
                DateTime prevTStamp = DateTime.MinValue;
                StreamReader sr = new StreamReader(LogFile);
                string logLine = sr.ReadLine();
                int tStamps = LoadProfileFromCsv(LogFile);
                int elapsedIndex = -1;
                string[] hdrArray = logLine.Split(new string[] { AppSettings.LoggerLogSeparator }, StringSplitOptions.None);
                for (int x = 0; x < hdrArray.Length; x++)
                {
                    if (hdrArray[x].ToLower().Contains("elapsed time"))
                    {
                        elapsedIndex = x;
                        break;
                    }
                }
 /*               for (int h=0; h<hdrArray.Length; h++)
                {
                    int count = 1;
                    for (int h2=h+1;h2<hdrArray.Length; h2++)
                    {
                        if (hdrArray[h2] == hdrArray[h])
                        {
                            Logger("Renaming duplicate column: " + hdrArray[h2] + " => ", false);
                            hdrArray[h2] = hdrArray[h] + " #" + count.ToString();
                            Logger(hdrArray[h2]);
                            count++;
                        }
                    }
                }*/
                int faketimestamps = 0;
                int row = 0;
                DateTime fDT = File.GetCreationTime(LogFile);
                string tStampDate = fDT.ToString("yyyy.MM.dd.");
                bool tStampErrorReported = false;
                TimestampFormat TSF = new TimestampFormat(fDT);
                while ((logLine = sr.ReadLine()) != null)
                {
                    string[] lParts = logLine.Split(new string[] { AppSettings.LoggerLogSeparator }, StringSplitOptions.None);
                    if (row < 2 && lParts.Length != hdrArray.Length)
                    {
                        throw new Exception(Environment.NewLine + "Column count don't match header. Check Log separator!");
                    }
                    if (lParts.Length != hdrArray.Length)
                    {
                        Logger("Columns don't match in row " + (row + 1).ToString() + ", skipping");
                        continue;
                    }
                    double val;
                    LogData ld = new LogData(lParts.Length - tStamps);
                    DateTime tStamp = TSF.ConvertTimeStamp(lParts[0]);
                    if (tStamp == fDT && !tStampErrorReported)
                    {
                        tStampErrorReported = true;
                        Logger("Timestamp format not match, Format: " + AppSettings.LoggerTimestampFormat + ", Timestamp: " + lParts[0]);
                    }
                    if (prevTStamp.Ticks >= tStamp.Ticks)
                    {
                        tStamp = prevTStamp.AddMilliseconds(1);
                        faketimestamps++;
                    }
                    prevTStamp = tStamp;
                    ld.TimeStamp = tStamp.Ticks;
                    if (startTime == DateTime.MinValue)
                    {
                        startTime = tStamp;
                    }
                    else if (elapsedIndex > -1)
                    {
                        string elapsedStr = lParts[elapsedIndex];
                        TimeSpan elapsed = TimeSpan.Parse(elapsedStr);
                        tStamp = startTime.Add(elapsed);
                    }
                    for (int r = tStamps; r < lParts.Length; r++)
                    {
                        if (string.IsNullOrEmpty(lParts[r]))
                        {
                            ld.Values[r - tStamps] = double.MinValue;
                        }
                        else
                        {
                            string valStr = lParts[r].Replace(",", ".");
                            if (OffStrings.Contains(valStr.ToLower()))
                                valStr = "0";
                            else if (OnStrings.Contains(valStr.ToLower()))
                                valStr = "1";

                            if (double.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out val))
                            {
                                ld.Values[r - tStamps] = val;
                            }
                            else
                            {
                                ld.Values[r - tStamps] = double.MinValue;
                            }
                        }
                    }
                    ld.CalculatedValues = ld.Values;
                    LogDataBuffer.Add(ld);
                    row++;
                }
                sr.Close();
                if (faketimestamps > (LogDataBuffer.Count/100))
                {
                    Logger(Environment.NewLine + "Timestamps adjusted, timing may not be accurate (" + faketimestamps.ToString() + " of " + LogDataBuffer.Count.ToString()+")");
                }
                Logger("[OK]");
            }
            catch (Exception ex)
            {
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(st.FrameCount - 1);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                LoggerBold("Error, LoadLogFile line " + line + ": " + ex.Message + ", inner exception: " + ex.InnerException);
            }
        }

        private bool SendQueuedCommand()
        {
            if (queuedCommands.Count == 0)
            {
                return true;
            }
            QueuedCommand command;
            lock (queuedCommands)
            {
                command = queuedCommands.Dequeue();
            }

            Application.DoEvents();
            //SetBusQuiet();
            //Thread.Sleep(10);
            datalogger.LogDevice.SetTimeout(TimeoutScenario.DataLogging3);
            //Thread.Sleep(10);
            //SetBusQuiet();
/*            OBDMessage resp = LogDevice.ReceiveMessage();
            while (resp != null)
            {
                resp = LogDevice.ReceiveMessage();
            }
*/
            switch (command.Cmd)
            {
                case QueueCmd.Getdtc:
                    RequestDTCCodes(command.param1, command.param2);
                    break;
                case QueueCmd.GetVin:
                    QueryVIN();
                    break;
                case QueueCmd.Custom:
                    Logger("Sending queued command: " + command.Description);
                    LogDevice.SendMessage(command.CustomMsg, 1);
                    //LogDevice.ReceiveMessage(true);
                    break;
            }
            if (passive)
            {
                //maxSlotsPerMessage = 4;
                LogDevice.SetTimeout(TimeoutScenario.DataLogging4);
                //AllSlotsRequested = false;
                //RequestPassiveModeSlots();
                SendTesterPresent(false);
            }
            else
            {
                if (LogDevice.LogDeviceType != LoggingDevType.Obdlink)
                {
                    LogDevice.SetTimeout(TimeoutScenario.DataLogging3);
                }

            }
            return true;
        }

        private void RequestMoreData()
        {
            if (passive)
            {
                if (maxSlotsPerRequest < maxPassiveSlotsPerRequest) //Started with fewer Slots
                {
                    if (LogDevice.LogDeviceType == LoggingDevType.Obdlink)
                    {
                        //Started with 4 Slots, now asking 50 more
                        RequestNextSlots();
                    }
                    Debug.WriteLine("Set Slots per request to: " + maxPassiveSlotsPerRequest);
                    maxSlotsPerRequest = maxPassiveSlotsPerRequest;
                }
                SendTesterPresent(false);
            }
            else //SendOnce
            {
                if (!RequestNextSlots())    // (??)Works with Obdlink, because we know how many Slots are coming
                    throw new Exception("Error in Slot request");
            }
        }

        public void DataLoggingLoop()
        {
            Thread.CurrentThread.IsBackground = true;
            int totalSlots = 0;
            long prevSlotTime = 0;
            DateTime LastRecvTime = DateTime.Now;
            AllSlotsRequested = false;
            Logger("Logging started");

            while (!logToken.IsCancellationRequested)
            {
                try
                {
                    int SlotCount = 0;
                    int retryCount = 0; //Receiving ok?
                    while ( SlotCount < maxSlotsPerRequest) //Loop there until requested Slots are handled
                    {
                        if (LogDevice == null || !Connected)
                        {
                            break;
                        }
                        if (stopLogLoop) 
                        {
                            if (LogDevice.LogDeviceType == LoggingDevType.Obdlink || LogDevice.LogDeviceType == LoggingDevType.Elm)
                            {
                                //Elm device can stop only when prompt received
                                Debug.WriteLine("Stopping soon, waiting elm prompt");
                            }
                            else
                            {
                                Debug.WriteLine("Stopping...");
                                break;
                            }
                        }
                        if (passive)
                        {
                            SendTesterPresent(false);
                        }

                        OBDMessage oMsg;
                        oMsg= LogDevice.ReceiveLogMessage();
                        if (oMsg == null)
                        {
                            Debug.WriteLine("Received null message");
                            if (DateTime.Now.Subtract(LastRecvTime) > TimeSpan.FromSeconds(AppSettings.LoggerRetryAfterSeconds))
                            {
                                if (retryCount < AppSettings.LoggerRetryCount)
                                {
                                    Logger("Data not received in "+AppSettings.LoggerRetryAfterSeconds.ToString()+" seconds, sending new request");
                                    RequestFirstSlots();
                                    LastRecvTime = DateTime.Now;
                                    retryCount++;
                                }
                                else
                                {
                                    LoggerBold("No data after retries, giving up");
                                    stopLogLoop = true;
                                    break;
                                }
                            }
                            continue;
                        }
                        Debug.WriteLine("Received: " + oMsg.ToString() +", Elmprompt: " + oMsg.ElmPrompt + " Slot count: " +SlotCount.ToString());
                        if (oMsg.ElmPrompt)
                        {
                            if (stopLogLoop)
                            {
                                break;
                            }
                            ELMPromptReceived();
                            continue;
                        }
                        if (!ValidateLogMessage(oMsg,ref SlotCount))
                        {
                            continue;
                        }
                        //We really have received a Slot!
                        SlotCount++;
                        totalSlots++;
                        LastRecvTime = DateTime.Now;
                        if (totalSlots == 10)
                        {
                            prevSlotTime = oMsg.TimeStamp;
                        }
                        if (totalSlots == 110)
                        {
                            long tLast = oMsg.TimeStamp;
                            long SlotDelay = tLast - prevSlotTime;
                            Debug.WriteLine("Time for 100 Slots: " + (SlotDelay / 10000).ToString() + " ms");
                        }
                        Debug.WriteLine("Decoding message");
                        slothandler.HandleSlotMessage(oMsg);
                        Application.DoEvents();
                    } //Inner logloop
                    if (stopLogLoop)
                    {
                        break;
                    }

                    if (!SendQueuedCommand())
                    {
                        return;     //If receieved Stop-command, return
                    }
                    RequestMoreData();
                }
                catch (Exception ex)
                {
                    var st = new StackTrace(ex, true);
                    // Get the top stack frame
                    var frame = st.GetFrame(st.FrameCount - 1);
                    // Get the line number from the stack frame
                    var line = frame.GetFileLineNumber();
                    Debug.WriteLine("Error, logLoop line " + line + ": " + ex.Message);
                    if (LogDevice == null || !Connected)
                    {
                        return;
                    }
                }
            } //Logloop
            Logger("Logging stopped");
            if (LogDevice != null && Connected)
            {
                LogDevice.ClearLogQueue();
                SetBusQuiet();
                SetBusQuiet();
                LogDevice.SetTimeout(TimeoutScenario.Maximum);
                //LogDevice.SetReadTimeout(AppSettings.TimeoutReceive);
                //LogDevice.SetWriteTimeout(AppSettings.TimeoutScriptWrite);
            }
            LogRunning = false;
            return;
        }


    }
}

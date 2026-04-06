using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IAP_OTA_Remote_Upgrade_UpperComputer
{
    public partial class Form1 : Form
    {
        public const UInt16 HEADER = 0xAA55;

        // 定义协议命令码
        public enum OtaCommand : ushort
        {
            CMD_PING = 0x0100,
            CMD_RESET = 0x0101,
            CMD_GET_VERSION = 0x0102,
            CMD_PARAM_READ = 0x0500,
            CMD_PARAM_WRITE = 0x0501,
            CMD_UPDATE_START = 0x0300,
            CMD_UPDATE_DATA = 0x0301,
            CMD_UPDATE_END = 0x0302,
            CMD_UPDATE_ABORT = 0x0303,
            CMD_ACK = 0xFF00,
            CMD_NACK = 0xFF01,
            CMD_BUSY = 0xFF02
        }

        private const int UpgradeChunkSize = 240; // UPDATE_DATA: 240字节数据，对应整帧252字节(240+12)
        private const uint DefaultTarget = 1;   // 1=APP_AREA_A, 2=APP_AREA_B
        private const uint DefaultVersion = 1;

        private readonly SerialPort sp1 = new SerialPort();
        private TcpClient _tcpClient;
        private readonly List<byte> _rxFrameBuffer = new List<byte>();
        private readonly object _rxLock = new object();
        private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _utf8RxBuffer = new StringBuilder();
        private readonly object _utf8LogLock = new object();
        private readonly SemaphoreSlim _singleFlightLock = new SemaphoreSlim(1, 1);
        private readonly AutoResetEvent _responseEvent = new AutoResetEvent(false);
        private readonly object _sessionLock = new object();
        private readonly object _transportLock = new object();

        private bool _isUpgrading = false;
        private bool _abortRequested = false;
        private PendingCommandSession _pendingSession;
        private NetworkStream _tcpStream;
        private CancellationTokenSource _tcpReceiveCts;
        private Task _tcpReceiveTask;
        private TransportKind _activeTransport = TransportKind.None;

        private enum CommandResultKind
        {
            None = 0,
            Ack = 1,
            Nack = 2,
            Busy = 3,
            Timeout = 4,
            SendFailed = 5,
            Aborted = 6,
            Echo = 7
        }

        private enum TransportKind
        {
            None = 0,
            Serial = 1,
            Tcp = 2
        }

        private sealed class PendingCommandSession
        {
            public OtaCommand RequestCmd;
            public bool ExpectEcho;
            public int TimeoutMs;
            public DateTime StartUtc;
            public CommandResultKind Result;
            public ushort ResponseCmd;
            public byte[] ResponseData;
        }

        private sealed class CommandSessionResult
        {
            public CommandResultKind Result;
            public ushort ResponseCmd;
            public byte[] ResponseData;
            public int BusyCount;
            public int RetryCount;
            public long ElapsedMs;

            public bool IsAckLikeSuccess
            {
                get
                {
                    return Result == CommandResultKind.Ack || Result == CommandResultKind.Echo;
                }
            }
        }

        public Form1()
        {
            InitializeComponent();
        }

        private void groupBox1_Enter(object sender, EventArgs e)
        {

        }

        private void openFileDialog1_FileOk(object sender, CancelEventArgs e)
        {

        }

        private void button4_Click(object sender, EventArgs e)
        {
            Task.Run(() =>
            {
                var result = SendCommandInSingleFlight(OtaCommand.CMD_PING, Encoding.ASCII.GetBytes("PING"), 2000, true, 0);
                HandleSessionResult(OtaCommand.CMD_PING, result);
            });
        }

        /// <summary>
        /// 基于协议格式打包数据，并发送到当前激活的传输通道（串口/TCP）
        /// </summary>
        private bool SendProtocolData(OtaCommand command, byte[] data = null)
        {
            if (_activeTransport == TransportKind.None)
            {
                AppendLog("[ERR] 请先打开串口或连接TCP！", Color.Red);
                return false;
            }

            try
            {
                byte[] packet = BuildFrame(command, data);

                if (_activeTransport == TransportKind.Serial)
                {
                    if (!sp1.IsOpen)
                    {
                        AppendLog("[ERR] 串口未打开", Color.Red);
                        return false;
                    }

                    sp1.Write(packet, 0, packet.Length);
                }
                else if (_activeTransport == TransportKind.Tcp)
                {
                    lock (_transportLock)
                    {
                        if (_tcpStream == null || _tcpClient == null || !_tcpClient.Connected)
                        {
                            AppendLog("[ERR] TCP未连接", Color.Red);
                            return false;
                        }

                        _tcpStream.Write(packet, 0, packet.Length);
                        _tcpStream.Flush();
                    }
                }
                else
                {
                    AppendLog("[ERR] 未知传输通道", Color.Red);
                    return false;
                }

                // 发送日志：显示命令和长度（不显示HEX）
                int dataLen = data == null ? 0 : data.Length;
                AppendLog($"[TX-{_activeTransport}] {command}, Length={dataLen}", Color.Blue);

                return true;
            }
            catch (Exception ex)
            {
                AppendLog("[ERR] 发送数据失败: " + ex.Message, Color.Red);
                return false;
            }
        }

        private void ProcessIncomingBytes(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
            {
                return;
            }

            // 升级期间屏蔽UTF-8日志解析，避免ASCII日志与二进制帧混流时干扰观察
            if (!_isUpgrading)
            {
                AppendUtf8LinesFromBytes(buffer);
            }

            // 追加到接收缓存并解析完整帧
            lock (_rxLock)
            {
                _rxFrameBuffer.AddRange(buffer);
                ParseReceivedFrames();
            }
        }

        private void StartTcpReceiveLoop()
        {
            StopTcpReceiveLoop();
            _tcpReceiveCts = new CancellationTokenSource();
            var token = _tcpReceiveCts.Token;
            _tcpReceiveTask = Task.Run(() => TcpReceiveLoop(token), token);
        }

        private void StopTcpReceiveLoop()
        {
            try
            {
                if (_tcpReceiveCts != null)
                {
                    _tcpReceiveCts.Cancel();
                }
            }
            catch { }
        }

        private void CloseTcpTransport()
        {
            try { StopTcpReceiveLoop(); } catch { }

            lock (_transportLock)
            {
                try
                {
                    if (_tcpStream != null)
                    {
                        _tcpStream.Close();
                        _tcpStream = null;
                    }
                }
                catch { }

                try
                {
                    if (_tcpClient != null)
                    {
                        _tcpClient.Close();
                        _tcpClient = null;
                    }
                }
                catch { }
            }

            _tcpStream = null;
            _tcpReceiveTask = null;
            _tcpReceiveCts = null;

            if (_activeTransport == TransportKind.Tcp)
            {
                _activeTransport = TransportKind.None;
            }
        }

        private void OpenTcpTransport(string host, int port)
        {
            lock (_transportLock)
            {
                if (_tcpClient != null && _tcpClient.Connected)
                {
                    CloseTcpTransport();
                }

                _tcpClient = new TcpClient();
                _tcpClient.Connect(host, port);
                _tcpStream = _tcpClient.GetStream();
                // 不对读超时做强制断链；lwIP端可能在空闲时不立即回包
                _tcpStream.ReadTimeout = Timeout.Infinite;
                _tcpStream.WriteTimeout = 3000;
            }

            _activeTransport = TransportKind.Tcp;
            StartTcpReceiveLoop();
        }

        private void TcpReceiveLoop(CancellationToken token)
        {
            byte[] buffer = new byte[4096];

            try
            {
                while (!token.IsCancellationRequested)
                {
                    NetworkStream stream;
                    lock (_transportLock)
                    {
                        stream = _tcpStream;
                    }

                    if (stream == null)
                    {
                        break;
                    }

                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    byte[] chunk = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                    ProcessIncomingBytes(chunk);
                }
            }
            catch (IOException ex)
            {
                // 读超时/暂时无数据不应视为断链
                var sockEx = ex.InnerException as SocketException;
                if (sockEx != null)
                {
                    if (sockEx.SocketErrorCode == SocketError.TimedOut || sockEx.SocketErrorCode == SocketError.WouldBlock)
                    {
                        return;
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    AppendLog("[TCP] 接收线程退出: " + ex.Message, Color.DarkOrange);
                }
            }
            catch (SocketException ex)
            {
                if (!token.IsCancellationRequested)
                {
                    if (ex.SocketErrorCode != SocketError.TimedOut && ex.SocketErrorCode != SocketError.WouldBlock)
                    {
                        AppendLog("[TCP] 接收线程退出: " + ex.Message, Color.DarkOrange);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // 正常关闭流程
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    AppendLog("[TCP] 接收线程退出: " + ex.Message, Color.DarkOrange);
                }
            }
            finally
            {
                if (_activeTransport == TransportKind.Tcp)
                {
                    AppendLog("[TCP] 连接已断开", Color.DarkOrange);
                    _activeTransport = TransportKind.None;
                    this.BeginInvoke(new Action(() =>
                    {
                        button1.Text = "连接/断开";
                    }));
                }
            }
        }

        private byte[] BuildFrame(OtaCommand command, byte[] data)
        {
            List<byte> frame = new List<byte>();

            // 1. Header (2 bytes) - 0xAA55 (线上发送: 55 AA)
            frame.AddRange(BitConverter.GetBytes((ushort)HEADER));

            // 2. Command (2 bytes)
            frame.AddRange(BitConverter.GetBytes((ushort)command));

            // 3. Length (2 bytes)
            ushort dataLen = (ushort)(data == null ? 0 : data.Length);
            frame.AddRange(BitConverter.GetBytes(dataLen));

            // 4. Data (N bytes)
            if (dataLen > 0)
            {
                frame.AddRange(data);
            }

            // 5. Reserved (4 bytes)
            frame.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });
            
            // 6. CRC16 (2 bytes)
            ushort crc = CalculateCRC16(frame.ToArray());
            frame.AddRange(BitConverter.GetBytes(crc));

            return frame.ToArray();
        }

        /// <summary>
        /// 计算 CRC16-MODBUS 校验码
        /// </summary>
        private ushort CalculateCRC16(byte[] data)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc >>= 1;
                        crc ^= 0xA001;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }
            return crc;
        }

        private uint CalculateCrc32Stm(byte[] alignedData)
        {
            uint crc = 0xFFFFFFFF;

            for (int i = 0; i < alignedData.Length; i += 4)
            {
                uint word = (uint)(alignedData[i]
                    | (alignedData[i + 1] << 8)
                    | (alignedData[i + 2] << 16)
                    | (alignedData[i + 3] << 24));

                crc ^= word;
                for (int bit = 0; bit < 32; bit++)
                {
                    if ((crc & 0x80000000) != 0)
                    {
                        crc = (crc << 1) ^ 0x04C11DB7;
                    }
                    else
                    {
                        crc <<= 1;
                    }
                }
            }

            return crc;
        }

        private byte[] PadTo4Bytes(byte[] raw)
        {
            int rem = raw.Length % 4;
            if (rem == 0)
            {
                return raw;
            }

            int pad = 4 - rem;
            byte[] aligned = new byte[raw.Length + pad];
            Buffer.BlockCopy(raw, 0, aligned, 0, raw.Length);
            for (int i = raw.Length; i < aligned.Length; i++)
            {
                aligned[i] = 0xFF;
            }

            return aligned;
        }

        private int GetBackoffDelayMs(int attemptIndex, int minDelayMs, int maxDelayMs)
        {
            if (attemptIndex <= 0)
            {
                return minDelayMs;
            }

            int delay = minDelayMs + attemptIndex * 50;
            return Math.Min(delay, maxDelayMs);
        }

        private CommandSessionResult SendCommandInSingleFlight(
            OtaCommand cmd,
            byte[] data,
            int timeoutMs,
            bool expectEcho = false,
            int maxBusyRetry = 0,
            int busyMinDelayMs = 100,
            int busyMaxDelayMs = 500)
        {
            var result = new CommandSessionResult
            {
                Result = CommandResultKind.None,
                ResponseData = new byte[0]
            };

            _singleFlightLock.Wait();
            var sw = Stopwatch.StartNew();
            try
            {
                int busyCount = 0;
                int retryCount = 0;

                for (int attempt = 0; attempt <= maxBusyRetry; attempt++)
                {
                    while (_responseEvent.WaitOne(0)) { }

                    var pending = new PendingCommandSession
                    {
                        RequestCmd = cmd,
                        ExpectEcho = expectEcho,
                        TimeoutMs = timeoutMs,
                        StartUtc = DateTime.UtcNow,
                        Result = CommandResultKind.None,
                        ResponseData = new byte[0]
                    };

                    lock (_sessionLock)
                    {
                        _pendingSession = pending;
                    }

                    if (!SendProtocolData(cmd, data))
                    {
                        lock (_sessionLock)
                        {
                            _pendingSession = null;
                        }

                        result.Result = CommandResultKind.SendFailed;
                        break;
                    }

                    bool signaled = false;
                    int waited = 0;
                    const int slice = 50;
                    while (waited < timeoutMs)
                    {
                        if (_abortRequested && cmd != OtaCommand.CMD_UPDATE_ABORT)
                        {
                            lock (_sessionLock)
                            {
                                _pendingSession = null;
                            }

                            result.Result = CommandResultKind.Aborted;
                            result.BusyCount = busyCount;
                            result.RetryCount = retryCount;
                            result.ElapsedMs = sw.ElapsedMilliseconds;
                            return result;
                        }

                        int waitMs = Math.Min(slice, timeoutMs - waited);
                        if (_responseEvent.WaitOne(waitMs))
                        {
                            signaled = true;
                            break;
                        }

                        waited += waitMs;
                    }

                    PendingCommandSession finished;
                    lock (_sessionLock)
                    {
                        finished = _pendingSession;
                        _pendingSession = null;
                    }

                    if (!signaled || finished == null || finished.Result == CommandResultKind.None)
                    {
                        result.Result = CommandResultKind.Timeout;
                        break;
                    }

                    result.Result = finished.Result;
                    result.ResponseCmd = finished.ResponseCmd;
                    result.ResponseData = finished.ResponseData ?? new byte[0];

                    if (finished.Result == CommandResultKind.Busy)
                    {
                        busyCount++;
                        if (attempt < maxBusyRetry)
                        {
                            retryCount++;
                            int delayMs = GetBackoffDelayMs(attempt, busyMinDelayMs, busyMaxDelayMs);
                            AppendLog($"[协议] {cmd} 收到 BUSY，{delayMs}ms 后退避重试({retryCount}/{maxBusyRetry})", Color.DarkOrange);
                            Thread.Sleep(delayMs);
                            continue;
                        }
                    }

                    break;
                }

                result.BusyCount = busyCount;
                result.RetryCount = retryCount;
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }
            finally
            {
                _singleFlightLock.Release();
            }
        }

        private bool HandleSessionResult(OtaCommand cmd, CommandSessionResult result)
        {
            if (result == null)
            {
                AppendLog($"[ERR] {cmd} 无结果", Color.Red);
                return false;
            }

            if (result.IsAckLikeSuccess)
            {
                return true;
            }

            if (result.Result == CommandResultKind.Busy)
            {
                AppendLog($"[ERR] {cmd} 收到 BUSY，重试已达上限", Color.Red);
                return false;
            }

            if (result.Result == CommandResultKind.Nack)
            {
                AppendLog($"[ERR] {cmd} 收到 NACK", Color.Red);
                return false;
            }

            if (result.Result == CommandResultKind.Timeout)
            {
                AppendLog($"[ERR] 等待 {cmd} 响应超时", Color.Red);
                return false;
            }

            if (result.Result == CommandResultKind.Aborted)
            {
                AppendLog($"[ERR] {cmd} 被本地 ABORT 中断", Color.OrangeRed);
                return false;
            }

            AppendLog($"[ERR] {cmd} 失败, Result={result.Result}", Color.Red);
            return false;
        }

        private void SetUpgradeIdleImmediately(string reason)
        {
            _abortRequested = true;
            _isUpgrading = false;
            this.BeginInvoke(new Action(() =>
            {
                button5.Enabled = true;
            }));

            AppendLog("[UPG-ABORT] 本地会话已回到 idle: " + reason, Color.DarkOrange);
        }

        private void SendAbortNow(string reason)
        {
            if (_activeTransport == TransportKind.None)
            {
                AppendLog("[UPG-ABORT] 当前无可用通道，无法发送 ABORT", Color.Red);
                return;
            }

            SetUpgradeIdleImmediately(reason);

            Task.Run(() =>
            {
                var abortResult = SendCommandInSingleFlight(OtaCommand.CMD_UPDATE_ABORT, null, 3000, false, 10, 100, 500);

                if (abortResult.Result == CommandResultKind.Ack)
                {
                    AppendLog("[UPG-ABORT] 设备返回 ACK，升级会话已中止", Color.DarkGreen);
                }
                else if (abortResult.Result == CommandResultKind.Nack)
                {
                    AppendLog("[UPG-ABORT] 设备返回 NACK（可能未处于升级态）", Color.OrangeRed);
                }
                else if (abortResult.Result == CommandResultKind.Busy)
                {
                    AppendLog("[UPG-ABORT] 设备返回 BUSY（通道占用）", Color.DarkOrange);
                }
                else if (abortResult.Result == CommandResultKind.Timeout)
                {
                    AppendLog("[UPG-ABORT] 等待设备响应超时", Color.Red);
                }
                else
                {
                    AppendLog("[UPG-ABORT] 发送失败: " + abortResult.Result, Color.Red);
                }
            });
        }

        private bool RunUpgrade(string filePath, uint targetArea)
        {
            bool startAcked = false;
            int totalPackets = 0;
            int busyTimes = 0;
            int retryTimes = 0;
            int failedPacket = -1;
            string failedReason = "-";
            Stopwatch totalSw = Stopwatch.StartNew();

            try
            {
                _abortRequested = false;

                byte[] raw = File.ReadAllBytes(filePath);
                byte[] aligned = PadTo4Bytes(raw);
                uint crc32 = CalculateCrc32Stm(aligned);

                AppendLog($"[UPG] 原始大小: {raw.Length} 字节", Color.Black);
                AppendLog($"[UPG] 对齐大小: {aligned.Length} 字节", Color.Black);
                AppendLog($"[UPG] CRC32: 0x{crc32:X8}", Color.Black);

                this.Invoke(new Action(() =>
                {
                    label2.Text = $"对齐大小: {aligned.Length} B";
                    label3.Text = $"CRC32: 0x{crc32:X8}";
                    progressBar1.Minimum = 0;
                    progressBar1.Maximum = aligned.Length;
                    progressBar1.Value = 0;
                }));

                // Start: target/version/size/crc32 (16字节, 小端)
                List<byte> startData = new List<byte>(16);
                startData.AddRange(BitConverter.GetBytes(targetArea));
                startData.AddRange(BitConverter.GetBytes(DefaultVersion));
                startData.AddRange(BitConverter.GetBytes((uint)aligned.Length));
                startData.AddRange(BitConverter.GetBytes(crc32));

                var startResult = SendCommandInSingleFlight(OtaCommand.CMD_UPDATE_START, startData.ToArray(), 8000, false, 10, 100, 500);
                busyTimes += startResult.BusyCount;
                retryTimes += startResult.RetryCount;
                if (!HandleSessionResult(OtaCommand.CMD_UPDATE_START, startResult))
                {
                    failedReason = "START失败:" + startResult.Result;
                    return false;
                }
                startAcked = true;

                int sent = 0;
                int packetIndex = 0;
                while (sent < aligned.Length)
                {
                    if (_abortRequested)
                    {
                        failedReason = "用户触发ABORT";
                        return false;
                    }

                    int len = Math.Min(UpgradeChunkSize, aligned.Length - sent);
                    byte[] chunk = new byte[len];
                    Buffer.BlockCopy(aligned, sent, chunk, 0, len);

                    packetIndex++;
                    totalPackets++;
                    Stopwatch pktSw = Stopwatch.StartNew();
                    var dataResult = SendCommandInSingleFlight(OtaCommand.CMD_UPDATE_DATA, chunk, 2000, false, 10, 100, 500);
                    pktSw.Stop();

                    busyTimes += dataResult.BusyCount;
                    retryTimes += dataResult.RetryCount;
                    string pktResultText = dataResult.Result.ToString().ToUpperInvariant();
                    AppendLog($"[UPG-PKT] #{packetIndex}, Len={len}, Cost={pktSw.ElapsedMilliseconds}ms, Result={pktResultText}, Busy={dataResult.BusyCount}, Retry={dataResult.RetryCount}",
                        dataResult.IsAckLikeSuccess ? Color.DarkGreen : Color.Red);

                    if (!HandleSessionResult(OtaCommand.CMD_UPDATE_DATA, dataResult))
                    {
                        failedPacket = packetIndex;
                        failedReason = "DATA失败:" + dataResult.Result;
                        return false;
                    }

                    sent += len;
                    this.Invoke(new Action(() =>
                    {
                        progressBar1.Value = sent;
                    }));
                }

                var endResult = SendCommandInSingleFlight(OtaCommand.CMD_UPDATE_END, null, 5000, false, 10, 100, 500);
                busyTimes += endResult.BusyCount;
                retryTimes += endResult.RetryCount;
                if (!HandleSessionResult(OtaCommand.CMD_UPDATE_END, endResult))
                {
                    failedReason = "END失败:" + endResult.Result;
                    return false;
                }

                AppendLog("[UPG] 升级完成，设备已通过校验", Color.DarkGreen);
                return true;
            }
            catch (Exception ex)
            {
                failedReason = "异常:" + ex.Message;
                AppendLog("[ERR] 升级异常: " + ex.Message, Color.Red);
                return false;
            }
            finally
            {
                totalSw.Stop();
                AppendLog($"[UPG-SUM] TotalPkt={totalPackets}, Busy={busyTimes}, Retry={retryTimes}, TotalCost={totalSw.ElapsedMilliseconds}ms, FailPkt={(failedPacket < 0 ? "-" : failedPacket.ToString())}, Reason={failedReason}", Color.DarkBlue);

                if (!string.Equals(failedReason, "-", StringComparison.Ordinal) && startAcked && !_abortRequested)
                {
                    SendAbortNow("升级失败自动ABORT");
                }
            }
        }

        private void AppendLog(string text, Color color)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => AppendLog(text, color)));
                return;
            }

            richTextBox1.SelectionColor = color;
            richTextBox1.AppendText(text + Environment.NewLine);
            richTextBox1.ScrollToCaret();
        }

        private void textBox3_TextChanged(object sender, EventArgs e)
        {

        }

        private void textBox2_TextChanged(object sender, EventArgs e)
        {

        }

        private void button3_Click(object sender, EventArgs e)
        {
            // 设置文件选择对话框
            openFileDialog1.Title = "请选择固件升级包";
            openFileDialog1.Filter = "Bin文件 (*.bin)|*.bin|所有文件 (*.*)|*.*";
            openFileDialog1.FileName = ""; // 清空默认文本

            // 如果用户点击了“确定”
            if (openFileDialog1.ShowDialog() == DialogResult.OK)
            {
                // 获取文件路径并显示在文本框
                string filePath = openFileDialog1.FileName;
                textBox3.Text = filePath;

                // 获取文件大小
                FileInfo fileInfo = new FileInfo(filePath);
                long fileSizeBytes = fileInfo.Length;

                // 在 label 处打印文件大小，格式化为字节和KB
                label1.Text = $"文件大小：{fileSizeBytes} 字节 ({fileSizeBytes / 1024.0:F2} KB)";

                // 选择文件后立即计算对齐大小和CRC32
                byte[] raw = File.ReadAllBytes(filePath);
                byte[] aligned = PadTo4Bytes(raw);
                uint crc32 = CalculateCrc32Stm(aligned);
                label2.Text = $"对齐大小: {aligned.Length} B";
                label3.Text = $"CRC32: 0x{crc32:X8}";
            }
        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void button2_Click(object sender, EventArgs e)
        {
            try
            {
                if (_activeTransport == TransportKind.Tcp)
                {
                    CloseTcpTransport();
                }

                if (!sp1.IsOpen)
                {
                    sp1.PortName = comboBox2.Text;
                    sp1.BaudRate = int.Parse(comboBox1.Text);
                    sp1.DataBits = 8;
                    sp1.StopBits = StopBits.One;
                    sp1.Parity = Parity.None;
                    sp1.Handshake = Handshake.None;
                    sp1.Encoding = Encoding.UTF8;

                    sp1.Open();
                    _activeTransport = TransportKind.Serial;
                    button2.Text = "关闭串口";
                    AppendLog("[SERIAL] 串口已打开，当前通道=UART", Color.DarkGreen);
                }
                else
                {
                    sp1.Close();
                    if (_activeTransport == TransportKind.Serial)
                    {
                        _activeTransport = TransportKind.None;
                    }
                    button2.Text = "打开串口";
                    AppendLog("[SERIAL] 串口已关闭", Color.DarkOrange);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("串口操作失败： " + ex.Message);
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            try
            {
                if (_activeTransport == TransportKind.Tcp)
                {
                    CloseTcpTransport();
                    button1.Text = "连接/断开";
                    return;
                }

                if (sp1.IsOpen)
                {
                    sp1.Close();
                    button2.Text = "打开串口";
                }

                string host = textBox1.Text.Trim();
                int port = int.Parse(textBox2.Text.Trim());

                OpenTcpTransport(host, port);
                button1.Text = "断开TCP";
                AppendLog($"[TCP] 已连接 {host}:{port}", Color.DarkGreen);
            }
            catch (Exception ex)
            {
                AppendLog("[TCP] 连接失败: " + ex.Message, Color.Red);
                CloseTcpTransport();
                button1.Text = "连接/断开";
            }
        }

        private void richTextBox1_TextChanged(object sender, EventArgs e)
        {

        }

        private void comboBox2_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void Form1_Load(object sender, EventArgs e)
        {
            comboBox1.Items.AddRange(new string[]
            {
               "115200",
               "57600",
               "38400",
               "19200",
               "9600"
            });

            // 默认选中 115200 (注意：115200在第0位)
            comboBox1.SelectedIndex = 0;

            comboBox2.Items.AddRange(SerialPort.GetPortNames());

            textBox1.Text = "127.0.0.1";
            textBox2.Text = "5000";

            button1.Click += button1_Click;

            // 绑定非升级指令到 comboBox3
            comboBox3.Items.AddRange(new string[]
            {
                "链路测试/心跳 (CMD_PING)",
                "请求设备软复位 (CMD_RESET)",
                "获取设备版本信息 (CMD_GET_VERSION)",
                "读取设备Flash参数区摘要 (CMD_PARAM_READ)"
            });
            comboBox3.SelectedIndex = 0;

            // 绑定升级目标分区到 comboBox4
            comboBox4.Items.AddRange(new string[]
            {
                "APP_A",
                "APP_B"
            });
            comboBox4.SelectedIndex = 0;

            label1.Text = "文件大小：-";
            label2.Text = "对齐大小：-";
            label3.Text = "CRC32：-";

            // 注册串口接收事件
            sp1.DataReceived += Sp1_DataReceived;
        }

        // 串口接收事件处理函数
        private void Sp1_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            int bytesToRead = sp1.BytesToRead;
            if (bytesToRead <= 0)
            {
                return;
            }

            byte[] buffer = new byte[bytesToRead];
            sp1.Read(buffer, 0, bytesToRead);

            ProcessIncomingBytes(buffer);
        }

        private void AppendUtf8LinesFromBytes(byte[] data)
        {
            lock (_utf8LogLock)
            {
                char[] chars = new char[Encoding.UTF8.GetMaxCharCount(data.Length)];
                int count = _utf8Decoder.GetChars(data, 0, data.Length, chars, 0, false);
                if (count <= 0)
                {
                    return;
                }

                _utf8RxBuffer.Append(chars, 0, count);

                int start = 0;
                for (int i = 0; i < _utf8RxBuffer.Length; i++)
                {
                    if (_utf8RxBuffer[i] == '\n')
                    {
                        string line = _utf8RxBuffer.ToString(start, i - start).TrimEnd('\r');
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            AppendLog("[RX-UTF8] " + line, Color.DarkCyan);
                        }
                        start = i + 1;
                    }
                }

                if (start > 0)
                {
                    _utf8RxBuffer.Remove(0, start);
                }

                // 对于没有换行但累计较长的内容，也输出一次，避免看起来无日志
                if (_utf8RxBuffer.Length >= 120)
                {
                    string tail = _utf8RxBuffer.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(tail))
                    {
                        AppendLog("[RX-UTF8] " + tail, Color.DarkCyan);
                    }
                    _utf8RxBuffer.Clear();
                }
            }
        }

        private void ParseReceivedFrames()
        {
            while (true)
            {
                if (_rxFrameBuffer.Count < 12)
                {
                    return;
                }

                int headerIndex = -1;
                for (int i = 0; i <= _rxFrameBuffer.Count - 2; i++)
                {
                    if (_rxFrameBuffer[i] == 0x55 && _rxFrameBuffer[i + 1] == 0xAA)
                    {
                        headerIndex = i;
                        break;
                    }
                }

                if (headerIndex < 0)
                {
                    _rxFrameBuffer.Clear();
                    return;
                }

                if (headerIndex > 0)
                {
                    _rxFrameBuffer.RemoveRange(0, headerIndex);
                    if (_rxFrameBuffer.Count < 12)
                    {
                        return;
                    }
                }

                ushort len = (ushort)(_rxFrameBuffer[4] | (_rxFrameBuffer[5] << 8));
                if (len > 256)
                {
                    _rxFrameBuffer.RemoveAt(0);
                    continue;
                }

                int frameLen = 12 + len;
                if (_rxFrameBuffer.Count < frameLen)
                {
                    return;
                }

                byte[] frame = _rxFrameBuffer.GetRange(0, frameLen).ToArray();
                _rxFrameBuffer.RemoveRange(0, frameLen);

                ushort recvCrc = BitConverter.ToUInt16(frame, frameLen - 2);
                ushort calcCrc = CalculateCRC16(frame.Take(frameLen - 2).ToArray());
                if (recvCrc != calcCrc)
                {
                    AppendLog("[ERR] RX帧CRC错误", Color.Red);
                    continue;
                }

                ushort cmd = BitConverter.ToUInt16(frame, 2);
                byte[] data = len > 0 ? frame.Skip(6).Take(len).ToArray() : new byte[0];

                bool matchedPending = false;
                PendingCommandSession pending;
                lock (_sessionLock)
                {
                    pending = _pendingSession;
                    if (pending != null)
                    {
                        TimeSpan age = DateTime.UtcNow - pending.StartUtc;
                        bool inWindow = age.TotalMilliseconds >= 0 && age.TotalMilliseconds <= pending.TimeoutMs + 200;
                        bool cmdMatched = cmd == (ushort)OtaCommand.CMD_ACK
                            || cmd == (ushort)OtaCommand.CMD_NACK
                            || cmd == (ushort)OtaCommand.CMD_BUSY
                            || (pending.ExpectEcho && cmd == (ushort)pending.RequestCmd);

                        if (inWindow && cmdMatched)
                        {
                            matchedPending = true;
                            pending.ResponseCmd = cmd;
                            pending.ResponseData = data;

                            if (cmd == (ushort)OtaCommand.CMD_ACK)
                            {
                                pending.Result = CommandResultKind.Ack;
                            }
                            else if (cmd == (ushort)OtaCommand.CMD_NACK)
                            {
                                pending.Result = CommandResultKind.Nack;
                            }
                            else if (cmd == (ushort)OtaCommand.CMD_BUSY)
                            {
                                pending.Result = CommandResultKind.Busy;
                            }
                            else
                            {
                                pending.Result = CommandResultKind.Echo;
                            }

                            _responseEvent.Set();
                        }
                    }
                }

                if (cmd == (ushort)OtaCommand.CMD_ACK)
                {
                    AppendLog("[协议] 收到 ACK（成功应答）", Color.DarkGreen);
                    TryParseAckData(data);
                }
                else if (cmd == (ushort)OtaCommand.CMD_NACK)
                {
                    AppendLog("[协议] 收到 NACK（失败应答）", Color.OrangeRed);
                }
                else if (cmd == (ushort)OtaCommand.CMD_BUSY)
                {
                    AppendLog("[协议] 收到 BUSY（设备忙/会话被占用）", Color.DarkOrange);
                }
                else if (cmd == (ushort)OtaCommand.CMD_PING)
                {
                    string pingText = ToUtf8Display(data);
                    AppendLog($"[协议] PING回显, Length={data.Length}, Data='{pingText}'", Color.DarkCyan);
                }
                else
                {
                    AppendLog($"[协议] 收到 Cmd=0x{cmd:X4}, Length={len}", Color.Gray);
                }

                if (!matchedPending && pending != null && (cmd == (ushort)OtaCommand.CMD_ACK || cmd == (ushort)OtaCommand.CMD_NACK || cmd == (ushort)OtaCommand.CMD_BUSY))
                {
                    AppendLog("[协议] 忽略未匹配当前会话窗口的应答帧", Color.Gray);
                }
            }
        }

        private void TryParseAckData(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }

            // VersionInfo_t: 4 + 4 + 4 + 12 = 24
            if (data.Length == 24)
            {
                uint bootVersion = BitConverter.ToUInt32(data, 0);
                uint appVersion = BitConverter.ToUInt32(data, 4);
                uint hwVersion = BitConverter.ToUInt32(data, 8);
                string buildDate = Encoding.ASCII.GetString(data, 12, 12).TrimEnd('\0', ' ');
                AppendLog($"[ACK-VER] boot={bootVersion}, app={appVersion}, hw={hwVersion}, build='{buildDate}'", Color.DarkSlateBlue);
                return;
            }

            // ParamSummary_t(packed): 4 + 4 + 4 + 1 + 1 = 14
            if (data.Length == 14)
            {
                uint bootVersion = BitConverter.ToUInt32(data, 0);
                uint bootRunCount = BitConverter.ToUInt32(data, 4);
                uint runAppVersion = BitConverter.ToUInt32(data, 8);
                byte appAStatus = data[12];
                byte appBStatus = data[13];
                AppendLog($"[ACK-PARAM] boot={bootVersion}, runCnt={bootRunCount}, runApp={runAppVersion}, A={appAStatus}, B={appBStatus}", Color.DarkSlateBlue);
                return;
            }

            AppendLog($"[ACK-DATA] Length={data.Length}", Color.DarkSlateBlue);
        }

        private string ToUtf8Display(byte[] data)
        {
            string text = Encoding.UTF8.GetString(data);
            StringBuilder sb = new StringBuilder(text.Length);

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r' || c == '\n' || c == '\t' || !char.IsControl(c))
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().Trim();
        }

        private void button7_Click(object sender, EventArgs e)
        {

        }

        private void button6_Click(object sender, EventArgs e)
        {
            richTextBox1.Clear();
        }

        private void button7_Click_1(object sender, EventArgs e)
        {

        }

        private void label1_Click(object sender, EventArgs e)
        {

        }

        private void groupBox4_Enter(object sender, EventArgs e)
        {

        }

        private void button7_Click_2(object sender, EventArgs e)
        {
            SendAbortNow("用户点击中止");
        }

        private void button7_Click_3(object sender, EventArgs e)
        {
            int selectedIndex = comboBox3.SelectedIndex;
            Task.Run(() =>
            {
                if (selectedIndex == 0)
                {
                    var ping = SendCommandInSingleFlight(OtaCommand.CMD_PING, Encoding.ASCII.GetBytes("PING"), 2000, true, 0);
                    HandleSessionResult(OtaCommand.CMD_PING, ping);
                }
                else if (selectedIndex == 1)
                {
                    var reset = SendCommandInSingleFlight(OtaCommand.CMD_RESET, null, 2000, false, 0);
                    HandleSessionResult(OtaCommand.CMD_RESET, reset);
                }
                else if (selectedIndex == 2)
                {
                    var ver = SendCommandInSingleFlight(OtaCommand.CMD_GET_VERSION, null, 2000, false, 0);
                    HandleSessionResult(OtaCommand.CMD_GET_VERSION, ver);
                }
                else if (selectedIndex == 3)
                {
                    var param = SendCommandInSingleFlight(OtaCommand.CMD_PARAM_READ, null, 2000, false, 0);
                    HandleSessionResult(OtaCommand.CMD_PARAM_READ, param);
                }
            });
        }

        private async void button5_Click(object sender, EventArgs e)
        {
            if (_isUpgrading)
            {
                MessageBox.Show("正在升级中，请勿重复点击。");
                return;
            }

            if (_activeTransport == TransportKind.None)
            {
                MessageBox.Show("请先打开串口或连接TCP。");
                return;
            }

            if (string.IsNullOrWhiteSpace(textBox3.Text) || !File.Exists(textBox3.Text))
            {
                MessageBox.Show("请先选择有效的 app.bin 文件。");
                return;
            }

            uint targetArea = GetSelectedTargetArea();

            _isUpgrading = true;
            _abortRequested = false;
            button5.Enabled = false;

            bool ok = await Task.Run(() => RunUpgrade(textBox3.Text, targetArea));

            _isUpgrading = false;
            button5.Enabled = true;

            MessageBox.Show(ok ? "升级成功" : "升级失败，请查看日志");

            if (ok)
            {
                await Task.Delay(200);
                await Task.Run(() =>
                {
                    var resetResult = SendCommandInSingleFlight(OtaCommand.CMD_RESET, null, 2000, false, 0);
                    HandleSessionResult(OtaCommand.CMD_RESET, resetResult);
                });
            }
       
        }

        private void comboBox3_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void label2_Click(object sender, EventArgs e)
        {

        }

        private void progressBar1_Click(object sender, EventArgs e)
        {

        }

        private void comboBox4_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private uint GetSelectedTargetArea()
        {
            if (comboBox4.SelectedIndex == 1)
            {
                return 2; // APP_B
            }

            return DefaultTarget; // APP_A
        }
    }
}

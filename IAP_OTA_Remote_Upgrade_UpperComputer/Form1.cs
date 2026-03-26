using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Text;
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
            CMD_ACK = 0xFF00,
            CMD_NACK = 0xFF01
        }

        SerialPort sp1 = new SerialPort();
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
            // 点击"握手/进入boot"时，发送链路测试/心跳包
            SendProtocolData(OtaCommand.CMD_PING);
        }

        /// <summary>
        /// 基于协议格式打包数据，并发送至串口
        /// </summary>
        private void SendProtocolData(OtaCommand command, byte[] data = null)
        {
            if (!sp1.IsOpen)
            {
                MessageBox.Show("请先打开串口！");
                return;
            }

            try
            {
                List<byte> frame = new List<byte>();

                // 1. Header (2 bytes) - 0xAA55 (C# BitConverter 会转为小端发送: 0x55, 0xAA)
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

                byte[] packet = frame.ToArray();

                // 发送数据
                sp1.Write(packet, 0, packet.Length);

                // 日志打印 TX 记录 (蓝色)
                string hexData = BitConverter.ToString(packet).Replace("-", " ") + " ";
                this.Invoke(new Action(() =>
                {
                    richTextBox1.SelectionColor = Color.Blue;
                    richTextBox1.AppendText("[TX] " + hexData + "\n");
                    richTextBox1.ScrollToCaret();
                }));
            }
            catch (Exception ex)
            {
                MessageBox.Show("发送数据失败: " + ex.Message);
            }
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
                System.IO.FileInfo fileInfo = new System.IO.FileInfo(filePath);
                long fileSizeBytes = fileInfo.Length;

                // 在 label 处打印文件大小，格式化为字节和KB
                label1.Text = $"文件大小：{fileSizeBytes} 字节 ({fileSizeBytes / 1024.0:F2} KB)";
            }
        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void button2_Click(object sender, EventArgs e)
        {
            try
            {
                if (!sp1.IsOpen)
                {
                    sp1.PortName = comboBox2.Text;
                    sp1.BaudRate = int.Parse(comboBox1.Text);
                    sp1.DataBits = 8;
                    sp1.StopBits = StopBits.One;
                    sp1.Parity = Parity.None;

                    sp1.Open();
                    button2.Text = "关闭串口";
                }
                else
                {
                    sp1.Close();
                    button2.Text = "打开串口";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("串口操作失败： " + ex.Message);
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

            // 绑定非升级指令到 comboBox3
            comboBox3.Items.AddRange(new string[] 
            {
                "链路测试/心跳 (CMD_PING)",
                "请求设备软复位 (CMD_RESET)",
                "获取设备版本信息 (CMD_GET_VERSION)",
                "读取设备Flash参数区摘要 (CMD_PARAM_READ)"
            });
            comboBox3.SelectedIndex = 0; // 默认选中第一个

            // 注册串口接收事件
            sp1.DataReceived += Sp1_DataReceived;
        }

        // 串口接收事件处理函数
        private void Sp1_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            // 读取缓冲区所有字节
            int bytesToRead = sp1.BytesToRead;
            byte[] buffer = new byte[bytesToRead];
            sp1.Read(buffer, 0, bytesToRead);

            // 转为十六进制字符串 (因为是OTA协议，以HEX展示更方便调试)
            string hexData = BitConverter.ToString(buffer).Replace("-", " ") + " ";

            // 使用 Invoke 跨线程安全更新 UI
            this.Invoke(new Action(() =>
            {
                richTextBox1.SelectionColor = Color.Green; // 接收数据用绿色显示
                richTextBox1.AppendText("[RX] " + hexData + "\n");
                richTextBox1.ScrollToCaret(); // 自动滚动
            }));
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

        }

        private void button7_Click_3(object sender, EventArgs e)
        {
            if (comboBox3.SelectedIndex == 0)
            {
                SendProtocolData(OtaCommand.CMD_PING);
            }
            else if (comboBox3.SelectedIndex == 1)
            {
                SendProtocolData(OtaCommand.CMD_RESET);
            }
            else if (comboBox3.SelectedIndex == 2)
            {
                SendProtocolData(OtaCommand.CMD_GET_VERSION);
            }
            else if (comboBox3.SelectedIndex == 3)
            {
                SendProtocolData(OtaCommand.CMD_PARAM_READ);
            }
        }

        private void button5_Click(object sender, EventArgs e)
        {

        }

        private void comboBox3_SelectedIndexChanged(object sender, EventArgs e)
        {

        }
    }
}

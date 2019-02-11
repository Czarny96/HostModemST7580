using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace HostModemST7580
{
    public enum AnalysisStatus
    {
        BEGIN, LENGTH, CC, DATA, CHECKSUM1, CHECKSUM2
    }

    public partial class MainWindow : Window
    {
        private AnalysisStatus analysisStatus;
        private SerialPort serialPortName = null;
        private Queue<byte> frameQueue = new Queue<byte>();
        //private Thread addTextToReceiveBox = null;
        private Thread receiver;
        private byte[] frameBuffer = new byte[256];
        private byte[] frameToAnalysis = new byte[256];
        private int frameIdx = 0;
        private int dataLength;
        private long time = 0;

        public MainWindow()
        {
            InitializeComponent();

            foreach (string port in SerialPort.GetPortNames())
                PortBox.Items.Add(port);

            OpenButton.IsEnabled = true;
            CloseButton.IsEnabled = false;
            SendButton.IsEnabled = false;
            ResetButton.IsEnabled = false;
        }
        
        private void OpenSelectedPort(object sender, RoutedEventArgs e)
        {
            if(PortBox.Items.Count > 0)
            {
                PortBox.SelectedIndex = PortBox.Items.Count;
                serialPortName = new SerialPort(PortBox.SelectedItem.ToString(), 57600, Parity.None, 8, StopBits.One);
                serialPortName.DataReceived += (new SerialDataReceivedEventHandler(ReceivedMessage));
                serialPortName.Open();

                OpenButton.IsEnabled = false;
                CloseButton.IsEnabled = true;
                SendButton.IsEnabled = true;
                ResetButton.IsEnabled = true;
                BPSK.IsChecked = true;

                DLButton.IsChecked = true;
                if(DLButton.IsChecked.Equals(true))
                    DL();

                receiver = new Thread(CheckNewValue);
                receiver.Start();
            }
        }

        private void CloseSelectedPort(object sender, RoutedEventArgs e)
        {
            if (serialPortName != null && serialPortName.IsOpen)
            {
                serialPortName.Close();
                receiver.Abort();
                OpenButton.IsEnabled = true;
                CloseButton.IsEnabled = false;
                SendButton.IsEnabled = false;
                DLButton.IsChecked = false;
                ResetButton.IsEnabled = false;
            }
        }

        public static byte[] MakeFrame(byte command, byte[] data = null)
        {
            int idx = 0;
            int dataLength = (data != null) ? data.Length : 0;

            byte[] frame = new byte[5 + dataLength];
            frame[idx++] = 0x02;
            frame[idx++] = (byte)dataLength;
            frame[idx++] = command;

            if (data != null)
                foreach (byte data_byte in data)
                    frame[idx++] = data_byte;

            int checksum = 0;
            for (int i = 1; i < idx; i++)
                checksum += frame[i];

            frame[idx++] = (byte)(checksum & 0x00FF);
            frame[idx] = (byte)(checksum >> 8);

            return frame;
        }

        public void SendFrame(byte[] frame)
        {
            serialPortName.RtsEnable = true;
            Thread.Sleep(10);
            serialPortName.Write(frame, 0, frame.Length);
            serialPortName.RtsEnable = false;
        }

        private void SendACK()
        {
            serialPortName.RtsEnable = true;
            Thread.Sleep(10);
            serialPortName.Write(new byte[] { 0x06 }, 0, 1);
            Thread.Sleep(10);
            serialPortName.RtsEnable = false;
        }

        private void SendMessage(object sender, RoutedEventArgs e)
        {
            if (serialPortName.IsOpen)
            {
                int lengthOfText = ASCIITextBox.Text.Length;

                if (lengthOfText > 0)
                {
                    lengthOfText += 1;
                    byte[] frame = new byte[lengthOfText];
                    string message = ASCIITextBox.Text;

                    if (frame.Equals(null) || frame.Length.Equals(0))
                        return;

                    byte[] buffer = new byte[frame.Length + 1];
                    byte configuration = 0x01;
                    byte[] mode = null;

                    if (BPSK.IsChecked.Equals(true))
                        configuration = 0x00;
                    else if (QPSK.IsChecked.Equals(true))
                        configuration <<= 4;
                    else if (PSK8.IsChecked.Equals(true))
                        configuration <<= 5;
                    else if (BPSKwithPNA.Equals(true))
                        configuration = 0x70;

                    if (FEC.IsChecked.Equals(true))
                        configuration |= 0x40;

                    configuration |= 0x04;

                    frame[0] = configuration;

                    for (int i = 1; i < lengthOfText; i++)
                        frame[i] = (byte)message[i - 1];

                    if (DLButton.IsChecked.Equals(true))
                        mode = MakeFrame(0x50, frame);
                    else if (PHYButton.IsChecked.Equals(true))
                        mode = MakeFrame(0x24, frame);

                    SendFrame(mode);
                }
            }
        }

        private void PHYChecked(object sender, RoutedEventArgs e)
        {
            byte[] frame = MakeFrame(0x08, new byte[] { 0x00, 0x10 });
            SendFrame(frame);
        }

        private void DLChecked(object sender, RoutedEventArgs e)
        {
            byte[] frame = MakeFrame(0x08, new byte[] { 0x00, 0x11 });
            SendFrame(frame);
        }

        private void DL()
        {
            byte[] frame = MakeFrame(0x08, new byte[] { 0x00, 0x11 });
            SendFrame(frame);
        }

        private void ResetModem(object sender, RoutedEventArgs e)
        {
            byte[] frame = MakeFrame(0x3C);
            SendFrame(frame);

            DLButton.IsChecked = true;
            Thread.Sleep(50);
            DL();
        }

        private void ASCIITextToHex(object sender, TextChangedEventArgs e)
        {
            if (ASCIITextBox.IsFocused)
            {
                HEXTextBox.Text = "";
                byte[] ascii = Encoding.ASCII.GetBytes(ASCIITextBox.Text);

                foreach (byte data in ascii)
                    HEXTextBox.Text += String.Format("{0:x} ", data);
            }
        }

        private void HEXTextToASCII(object sender, TextChangedEventArgs e)
        {
            if (HEXTextBox.IsFocused)
            {
                ASCIITextBox.Text = "";

                string[] hexText = HEXTextBox.Text.Split(' ');
                foreach (string @string in hexText)
                    if (@string != "")
                        try
                        {
                            int hex = Convert.ToInt32(@string, 16);
                            ASCIITextBox.Text += Convert.ToChar(hex);
                        }
                        catch (Exception)
                        {
                            ASCIITextBox.Text = "Invalid Format";
                        }
            }
        }

        private void AddTextToReceiveBox(byte[] frame)
        {
            if (frame[0] == 0x02)
            {
                string dataOutPut = "";

                for (int i = 7; i < frameIdx - 2; i++)
                    dataOutPut += (char)frame[i];
                
                dataOutPut += '\n';

                Dispatcher.BeginInvoke((Action)(() => ReceivedTextBox.Text += dataOutPut ));
                SendACK();
            }

            analysisStatus = AnalysisStatus.BEGIN;
        }

        private void CheckNewValue()
        {
            while (serialPortName.IsOpen)
            {
                while (frameQueue.Count > 0)
                {
                    byte byteFromQueue = frameQueue.Dequeue();
                    ReadByte(byteFromQueue);
                }
                Thread.Sleep(1);
            }
        }

        private void ReceivedMessage(object sender, SerialDataReceivedEventArgs e)
        {
            SerialPort port = (SerialPort)sender;
            int dataLength = port.BytesToRead;
            byte[] data = new byte[dataLength];
            port.Read(data, 0, dataLength);
            foreach (byte b in data)
                frameQueue.Enqueue(b);
        }

        private void ClearSendText(object sender, RoutedEventArgs e)
        {
            ASCIITextBox.Text = "";
            HEXTextBox.Text = "";
        }

        private void ClearReceivedText(object sender, RoutedEventArgs e)
        {
            ReceivedTextBox.Text = "";
        }

        private void TimerStart(long time)
        {
            this.time = time;
        }

        private void TimerStop()
        {
            this.time = 0;
        }

        private void ReadByte(byte byteData)
        {
            if (analysisStatus == AnalysisStatus.BEGIN)
            {
                frameIdx = 0;
                frameToAnalysis[frameIdx++] = byteData;

                if ((frameToAnalysis[0] != 0x02) && 
                    (frameToAnalysis[0] != 0x03) &&
                    (frameToAnalysis[0] != 0x06) && 
                    (frameToAnalysis[0] != 0x15) &&
                    (frameToAnalysis[0] != 0x3F))
                        frameIdx = 0;
                else
                {
                    if ((frameToAnalysis[0] == 0x06) || (frameToAnalysis[0] == 0x15))
                        AddTextToReceiveBox(frameToAnalysis);
                    else if ((frameToAnalysis[0] == 0x02) || (frameToAnalysis[0] == 0x03) || (frameToAnalysis[0] == 0x3F))
                    {
                        analysisStatus = AnalysisStatus.LENGTH;
                        TimerStart(10);
                    }
                }
            }
            else if (analysisStatus == AnalysisStatus.LENGTH)
            {
                frameToAnalysis[frameIdx++] = byteData;
                dataLength = byteData;

                if (frameToAnalysis[0] == 0x3F)
                {
                    analysisStatus = AnalysisStatus.BEGIN;
                    AddTextToReceiveBox(frameToAnalysis);
                    TimerStop();
                }
                else
                {
                    analysisStatus = AnalysisStatus.CC;
                    TimerStart(10);
                }
            }
            else if (analysisStatus == AnalysisStatus.CC)
            {
                frameToAnalysis[frameIdx++] = byteData;

                if (frameToAnalysis[1] != 0x00)
                    analysisStatus = AnalysisStatus.DATA;
                else
                    analysisStatus = AnalysisStatus.CHECKSUM1;

                TimerStart(10);
            }
            else if (analysisStatus == AnalysisStatus.DATA)
            {
                frameToAnalysis[frameIdx++] = byteData;
                dataLength--;
                TimerStart(10);

                if (dataLength == 0)
                    analysisStatus = AnalysisStatus.CHECKSUM1;
            }
            else if (analysisStatus == AnalysisStatus.CHECKSUM1)
            {
                frameToAnalysis[frameIdx++] = byteData;
                TimerStart(10);
                analysisStatus = AnalysisStatus.CHECKSUM2;
            }
            else if (analysisStatus == AnalysisStatus.CHECKSUM2)
            {
                frameToAnalysis[frameIdx++] = byteData;
                TimerStop();

                int checksum2 = 0;
                for (int i = 1; i < frameIdx - 2; i++)
                    checksum2 += frameToAnalysis[i];

                int checksum = frameToAnalysis[frameIdx - 1];

                checksum <<= 8;
                checksum |= frameToAnalysis[frameIdx - 2];

                if (checksum2 == checksum)
                    AddTextToReceiveBox(frameToAnalysis);

                analysisStatus = AnalysisStatus.BEGIN;
            }

            if (frameIdx > 256)
            {
                frameIdx = 0;
                TimerStart(1);
            }
        }
    }
}
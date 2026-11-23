// Servo tester for the Harp Behavior board: one slider per servo output.
//   DO2 = spout (front/back), DO3 = Y motor (medial/lateral).
// Talks to the board directly over the serial port, so Bonsai must be closed while this runs.
// Build with build.cmd (.NET Framework C# compiler, no extra packages).

using System;
using System.Drawing;
using System.IO.Ports;
using System.Windows.Forms;

class ServoTester : Form
{
    // Harp Behavior registers
    const byte EnableServos = 80;          // U8 bitmask, writing a bit enables that output
    const byte ServoMotor2Period = 100, ServoMotor2Pulse = 101;   // U16, microseconds
    const byte ServoMotor3Period = 102, ServoMotor3Pulse = 103;
    const byte ServoOutput2 = 4, ServoOutput3 = 8;

    SerialPort port;
    readonly ComboBox portBox;
    readonly Button connectButton;
    readonly Label status;

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new ServoTester());
    }

    ServoTester()
    {
        Text = "Servo tester (close Bonsai first)";
        ClientSize = new Size(560, 270);
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        portBox = new ComboBox { Left = 12, Top = 12, Width = 90, DropDownStyle = ComboBoxStyle.DropDown };
        portBox.Items.AddRange(SerialPort.GetPortNames());
        portBox.Text = "COM4";
        connectButton = new Button { Left = 110, Top = 10, Width = 110, Height = 30, Text = "Connect" };
        connectButton.Click += delegate { ToggleConnection(); };
        status = new Label { Left = 230, Top = 16, Width = 320, Text = "Not connected", ForeColor = Color.Gray };
        Controls.AddRange(new Control[] { portBox, connectButton, status });

        AddMotorRow("Spout (DO2)", ServoMotor2Pulse, 60, 1400);
        AddMotorRow("Y motor (DO3)", ServoMotor3Pulse, 160, 1500);
    }

    void AddMotorRow(string name, byte pulseRegister, int top, int start)
    {
        var label = new Label { Left = 12, Top = top, Width = 200, Text = name, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        var slider = new TrackBar { Left = 12, Top = top + 24, Width = 400, Minimum = 1000, Maximum = 2000, SmallChange = 10, LargeChange = 50, TickFrequency = 100, Value = start };
        var box = new NumericUpDown { Left = 420, Top = top + 28, Width = 70, Minimum = 1000, Maximum = 2000, Increment = 10, Value = start };
        var send = new Button { Left = 495, Top = top + 26, Width = 55, Height = 28, Text = "Send" };
        var hint = new Label { Left = 12, Top = top + 70, Width = 540, Text = "1000 ........................ 1500 ........................ 2000 us", ForeColor = Color.Gray, Font = new Font("Segoe UI", 8.5f) };

        bool syncing = false;
        // Slider moves send immediately (the servo just follows); the box sends on Enter, arrows or Send.
        slider.ValueChanged += delegate
        {
            if (syncing) return;
            syncing = true; box.Value = slider.Value; syncing = false;
            MoveServo(pulseRegister, slider.Value);
        };
        box.ValueChanged += delegate
        {
            if (syncing) return;
            syncing = true; slider.Value = (int)box.Value; syncing = false;
            MoveServo(pulseRegister, (int)box.Value);
        };
        send.Click += delegate { MoveServo(pulseRegister, (int)box.Value); };
        Controls.AddRange(new Control[] { label, slider, box, send, hint });
    }

    void ToggleConnection()
    {
        if (port != null) { Disconnect(); return; }
        try
        {
            port = new SerialPort(portBox.Text.Trim(), 1000000, Parity.None, 8, StopBits.One);
            port.Handshake = Handshake.RequestToSend;
            port.DataReceived += delegate { try { port.DiscardInBuffer(); } catch { } };   // ignore board replies
            port.Open();
            WriteU8(EnableServos, ServoOutput2 | ServoOutput3);
            WriteU16(ServoMotor2Period, 20000);
            WriteU16(ServoMotor3Period, 20000);
            connectButton.Text = "Disconnect";
            status.Text = "Connected to " + port.PortName + " - move a slider";
            status.ForeColor = Color.FromArgb(46, 160, 67);
        }
        catch (Exception e)
        {
            if (port != null) { try { port.Dispose(); } catch { } port = null; }
            status.Text = "Could not open port (Bonsai still open?)";
            status.ForeColor = Color.FromArgb(207, 34, 46);
            MessageBox.Show(this, e.Message, "Servo tester");
        }
    }

    void Disconnect()
    {
        try { port.Close(); } catch { }
        port = null;
        connectButton.Text = "Connect";
        status.Text = "Not connected";
        status.ForeColor = Color.Gray;
    }

    void MoveServo(byte register, int pulse)
    {
        if (port == null) return;
        WriteU16(register, (ushort)pulse);
        status.Text = (register == ServoMotor2Pulse ? "Spout" : "Y motor") + " -> " + pulse + " us";
    }

    // Harp write message: [type=2, length, address, port=255, payloadType, payload..., checksum]
    void WriteU8(byte address, byte value)
    {
        Send(new byte[] { 2, 5, address, 255, 1, value, 0 });
    }

    void WriteU16(byte address, ushort value)
    {
        Send(new byte[] { 2, 6, address, 255, 2, (byte)(value & 0xFF), (byte)(value >> 8), 0 });
    }

    void Send(byte[] msg)
    {
        byte sum = 0;
        for (int i = 0; i < msg.Length - 1; i++) sum += msg[i];
        msg[msg.Length - 1] = sum;
        try { port.Write(msg, 0, msg.Length); }
        catch (Exception e) { status.Text = "Write failed: " + e.Message; status.ForeColor = Color.FromArgb(207, 34, 46); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (port != null) Disconnect();
        base.OnFormClosing(e);
    }
}


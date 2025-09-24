using System;
using System.Text;
using System.IO.Ports;
using DotNetEnv;
using System.Collections.Generic;
using Serilog;

public class Program
{
  private static readonly byte ENQ = 5;
  private static readonly byte ACK = 6;
  private static readonly byte STX = 2;
  private static readonly byte ETX = 3;
  private static readonly byte CR = 13;
  private static readonly byte EOT = 4;
  private static readonly byte ETB = 23;
  private static readonly byte LF = 10;
  private static readonly byte NAK = 21;
  private static readonly int MAX_SEQUENCE = 7;
  private static int nextSequence = 1;

  // Buffer untuk menampung data paket
  private static readonly List<byte> packetBuffer = new List<byte>();
  private static string currentMessage = "";
  private static readonly List<string> messageCollections = new List<string>();



  static void Main(string[] args)
  {
    Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning) // Reduce framework noise
    .WriteTo.Console()
    .WriteTo.File("logs/myapp-.txt", rollingInterval: RollingInterval.Day) // Log to a daily rotating file
    .CreateLogger();

    Env.Load();
    string portName = Environment.GetEnvironmentVariable("SERIAL_PORT") ?? "/dev/pts/2";
    int baudRate = int.Parse(Environment.GetEnvironmentVariable("BAUD_RATE") ?? "9600");
    int dataBits = int.Parse(Environment.GetEnvironmentVariable("DATA_BITS") ?? "8");

    SerialPort serialPort = new SerialPort();
    serialPort.PortName = portName;
    serialPort.BaudRate = baudRate;
    serialPort.Parity = Parity.None;
    serialPort.DataBits = dataBits;
    serialPort.StopBits = StopBits.One;
    serialPort.Handshake = Handshake.None;

    serialPort.DataReceived += new SerialDataReceivedEventHandler(DataReceivedHandler);

    try
    {
      serialPort.Open();
      Console.WriteLine("Serial port opened.");
      Console.WriteLine("Press any key to exit...");
      Console.ReadKey();
      serialPort.Close();
    }
    catch (Exception e)
    {
      Console.WriteLine("Error: " + e.Message);
    }
  }

  private static void Reset()
  {
    nextSequence = 1;
    currentMessage = "";
    messageCollections.Clear();
    packetBuffer.Clear();
  }
  private static void LogPacket(byte[] packet)
  {
    string packetString = Encoding.Latin1.GetString(packet);
    Log.Information("Packet String: " + packetString.Replace("\r", "<CR>").Replace("\n", "<LF>").Replace(((char)STX).ToString(), "<STX>").Replace(((char)ETX).ToString(), "<ETX>").Replace(((char)ETB).ToString(), "<ETB>"));
  }
  private static string ExtractText(byte[] packet, bool tailed = false)
  {
    if (packet[0] != STX)
    {
      throw new ArgumentException("Packet must start with STX");
    }
    if (tailed && (packet.Length < 8 || packet[^5] != ETB))
    {
      throw new ArgumentException("Invalid packet format (tailed)");
    }
    if (!tailed && (packet.Length < 9 || packet[^5] != ETX))
    {
      throw new ArgumentException("Invalid packet format");
    }

    if (!tailed)
    {
      int crIndex = Array.IndexOf(packet, CR, 1);
      if (crIndex == -1 || crIndex != packet.Length - 6)
        throw new ArgumentException("Invalid packet format (tailed)");
      int textLength = crIndex - 1;
      int startIndex = 2;
      List<byte> textBytes = [.. packet[startIndex..(1 + textLength)]];
      return Encoding.Latin1.GetString([.. textBytes]);
    }
    else
    {
      int etbIndex = Array.IndexOf(packet, ETB, 1);
      int textLength = etbIndex - 1;
      int startIndex = 2;
      List<byte> textBytes = [.. packet[startIndex..(1 + textLength)]];
      return Encoding.Latin1.GetString([.. textBytes]);
    }
  }
  private static bool IsValidSequence(byte sequence)
  {
    return Encoding.Latin1.GetString([sequence]) == nextSequence.ToString();
  }
  private static string GenerateCheckSum(byte[] packet, byte endtx)
  {
    int indexSTX = Array.IndexOf(packet, STX);
    int indexENDTX = Array.LastIndexOf(packet, endtx);

    if (indexSTX == -1 || indexENDTX == -1 || indexENDTX <= indexSTX)
    {
      throw new ArgumentException("Invalid packet format");
    }

    // Content between STX and ENDTX
    byte[] contentBytes = packet[(indexSTX + 1)..(indexENDTX + 1)];
    string contentString = Encoding.Latin1.GetString(contentBytes);
    Log.Information("Index ENDTX: " + indexENDTX);
    Log.Information("Content String: " + contentString);
    Log.Information("Content Length: " + contentString.Length);

    int total = contentBytes.Sum(c => (int)c);
    string checkSum = (total % 256).ToString("X2");
    return checkSum;
  }
  private static bool IsValidChecksum(byte[] packet, byte endtx)
  {
    if (packet[^2] != CR || packet[^1] != LF)
    {
      return false;
    }

    int indexENDTX = Array.IndexOf(packet, endtx);

    List<byte> checkSumBytes = [.. packet.ToArray().Skip(indexENDTX + 1).Take(2)];

    string receivedCheckSum = Encoding.Latin1.GetString([.. checkSumBytes]);
    string calculatedCheckSum = GenerateCheckSum(packet, endtx);

    Log.Information("===Checksum Validation===");
    Log.Information("Calculated Checksum: " + calculatedCheckSum);
    Log.Information("Received Checksum: " + receivedCheckSum);

    if (!receivedCheckSum.Equals(calculatedCheckSum, StringComparison.OrdinalIgnoreCase))
    {
      if (endtx == ETX)
      {
        Console.WriteLine("Error in ETX packet");
      }
      else
      {
        Console.WriteLine("Error in ETB packet");
      }
    }

    return receivedCheckSum.Equals(calculatedCheckSum, StringComparison.OrdinalIgnoreCase);
  }
  private static void DataReceivedHandler(object sender, SerialDataReceivedEventArgs e)
  {
    SerialPort sp = (SerialPort)sender;
    int bytesToRead = sp.BytesToRead;
    byte[] buffer = new byte[bytesToRead];
    sp.Read(buffer, 0, bytesToRead);

    foreach (byte b in buffer)
    {
      if (b == ENQ && buffer.Length == 1)
      {
        Console.WriteLine("Received ENQ from Client.");
        sp.Write(new byte[] { ACK }, 0, 1);
        Console.WriteLine("Send ACK to Client.");
        packetBuffer.Clear();
      }
      else if (b == EOT && buffer.Length == 1)
      {
        Console.WriteLine("Received EOT from Client.");
        Console.WriteLine("Transmission ended.");
        string allMessages = string.Join("\n", messageCollections);
        Console.WriteLine("All Messages:\n" + allMessages);
        Reset();
      }
      else
      {
        packetBuffer.Add(b);

        // Jika sudah menerima CR & LF, proses paket
        if (b == LF)
        {
          try
          {
            LogPacket([.. packetBuffer]);
            // Get ENDTX is ETX or ETB
            byte endTx = packetBuffer[^5];
            if (endTx != ETX && endTx != ETB)
            {
              // throw new ArgumentException("Invalid packet format (missing ETX/ETB)");
              Console.WriteLine("Invalid packet format (missing ETX/ETB)");
              packetBuffer.Clear();
              sp.Write(new byte[] { NAK }, 0, 1);
              Console.WriteLine("Send NAK to Client.");
              return;
            }
            if (!IsValidSequence(packetBuffer[1]))
            {
              Console.WriteLine("Invalid sequence number. Expected: " + nextSequence + ", Received: " + (int)packetBuffer[1]);
              packetBuffer.Clear();
              sp.Write(new byte[] { NAK }, 0, 1);
              Console.WriteLine("Send NAK to Client.");
              return;
            }
            if (!IsValidChecksum([.. packetBuffer], endTx))
            {
              Console.WriteLine("Invalid checksum.");
              packetBuffer.Clear();
              sp.Write(new byte[] { NAK }, 0, 1);
              Console.WriteLine("Send NAK to Client.");
              return;
            }
            string text = ExtractText([.. packetBuffer], tailed: endTx == ETB);
            if (endTx == ETB)
            {
              Console.WriteLine("Data Received (Tailed): " + text);
              currentMessage += text;
              packetBuffer.Clear();
              sp.Write(new byte[] { ACK }, 0, 1);
              Console.WriteLine("Send ACK to Client.");

              nextSequence = (nextSequence + 1) % (MAX_SEQUENCE + 1);
            }
            else if (endTx == ETX)
            {
              text = currentMessage + text;
              messageCollections.Add(text);
              Console.WriteLine("Data Received: " + text);
              currentMessage = "";
              packetBuffer.Clear();
              sp.Write(new byte[] { ACK }, 0, 1);
              Console.WriteLine("Send ACK to Client.");

              nextSequence = (nextSequence + 1) % (MAX_SEQUENCE + 1);
            }
          }
          catch (Exception ex)
          {
            Console.WriteLine("Packet error: " + ex.Message);
            packetBuffer.Clear();
            sp.Write(new byte[] { NAK }, 0, 1);
            Console.WriteLine("Send NAK to Client.");
          }
        }
      }
    }
  }
}
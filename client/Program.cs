using System;
using System.IO.Ports;
using System.Text;
using DotNetEnv;
using System.Reflection.Metadata;
using Serilog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;



public class Program
{

  private static string TEXT = """
  Hello Morasaurus
  Lorem ipsum dolor sit amet, consectetur adipiscing elit. Donec eget laoreet eros.
  Halo Dunia
  Hello Dinosaurus
  """;
  private static readonly byte ENQ = 5;
  private static readonly byte ACK = 6;
  private static readonly byte ETB = 23;
  private static readonly byte EOT = 4;
  private static int line = 0;
  private static readonly byte STX = 2;
  private static readonly byte ETX = 3;
  private static readonly byte CR = 13;
  private static readonly byte LF = 10;
  private static readonly byte NAK = 21;
  private static int partition = 0;
  private static int lastPartition = partition;
  private static readonly int maxCharactersPerLine = 50;
  private static int sequence = 1;

  private static readonly int MAX_SEQUENCE = 7;

  private static bool running = true;

  private static void LogPacket(byte[] packet)
  {
    string packetString = Encoding.Latin1.GetString(packet);
    Log.Information("Packet String: " + packetString.Replace("\r", "<CR>").Replace("\n", "<LF>").Replace(((char)STX).ToString(), "<STX>").Replace(((char)ETX).ToString(), "<ETX>").Replace(((char)ETB).ToString(), "<ETB>"));
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
      Log.Information("=== Checksum Error Details ===");
      Log.Information("Received Checksum: " + receivedCheckSum);
      Log.Information("Calculated Checksum: " + calculatedCheckSum);
    }

    return receivedCheckSum.Equals(calculatedCheckSum, StringComparison.OrdinalIgnoreCase);
  }

  private static void Reset()
  {
    partition = 0;
    lastPartition = partition;
    sequence = 1;
  }
  private static string GetContentText(JArray structure, dynamic data, string sep = "|", string sequence = "1")
  {

    var listText = new List<string>();

    foreach (var item in structure)
    {
      JObject itemObj = (JObject)item;
      List<string> keys = itemObj.Properties().Select(p => p.Name).ToList();

      // we only use the first key in this structure
      string key = keys[0];
      JToken tokenNode = itemObj[key]!;

      string token = "";

      if (tokenNode.Type == JTokenType.String ||
          tokenNode.Type == JTokenType.Integer ||
          tokenNode.Type == JTokenType.Boolean ||
          tokenNode.Type == JTokenType.Float)
      {
        // JValue
        token = tokenNode?.ToString() ?? "";
        if (string.IsNullOrEmpty(token))
        {
          // fallback ke data
          token = data[key]?.ToString() ?? "";
        }
      }
      else if (tokenNode.Type == JTokenType.Array)
      {
        // recursive call for nested structure
        var structureChild = (JArray)itemObj[key]!;
        var dataChild = data[key];
        if (dataChild != null)
        {
          token = GetContentText(structureChild, dataChild, sep: "^", sequence: sequence);
        }
      }
      else if (tokenNode.Type == JTokenType.Object)
      {
        // nested JObject
        token = GetContentText(new JArray(tokenNode), data[key], sep: "^", sequence: sequence);
      }
      if (key == "Sequence No.")
      {
        token = sequence;
      }
      listText.Add(token);
    }

    return string.Join(sep, listText);
  }
  private static string GetHeaderText(JArray structure, dynamic data, string sep = "|")
  {
    var listText = new List<string>();

    foreach (var item in structure)
    {
      JObject itemObj = (JObject)item;
      List<string> keys = itemObj.Properties().Select(p => p.Name).ToList();

      // we only use the first key in this structure
      string key = keys[0];
      JToken tokenNode = itemObj[key];

      string token = "";

      if (tokenNode.Type == JTokenType.String ||
          tokenNode.Type == JTokenType.Integer ||
          tokenNode.Type == JTokenType.Boolean ||
          tokenNode.Type == JTokenType.Float)
      {
        // JValue
        token = tokenNode?.ToString() ?? "";
        if (string.IsNullOrEmpty(token))
        {
          // fallback ke data
          token = data[key]?.ToString() ?? "";
        }
      }
      else if (tokenNode.Type == JTokenType.Array)
      {
        // recursive call for nested structure
        var structureChild = (JArray)itemObj[key];
        var dataChild = data[key];

        if (dataChild != null)
        {
          token = GetHeaderText(structureChild, dataChild, "^");
        }
      }
      else if (tokenNode.Type == JTokenType.Object)
      {
        // nested JObject (rare case in your sample)
        token = GetHeaderText(new JArray(tokenNode), data[key], sep: "^");
      }

      listText.Add(token);
    }

    return string.Join(sep, listText);
  }
  private static JObject ParseContentText(JArray structure, string line, string sep = "|")
  {
    var obj = new JObject();
    string[] tokens = line.Split(sep);

    int index = 0;
    foreach (var field in structure)
    {
      JObject fieldObj = (JObject)field;
      string key = fieldObj.Properties().First().Name;
      JToken fieldDef = fieldObj[key];

      if (fieldDef.Type == JTokenType.String) // simple field
      {
        obj[key] = index < tokens.Length ? tokens[index] : "";
        index++;
      }
      else if (fieldDef.Type == JTokenType.Array) // nested array (like Universal Test ID)
      {
        string childLine = index < tokens.Length ? tokens[index] : "";
        index++;

        JArray childStructure = (JArray)fieldDef;
        JObject childObj = ParseContentText(childStructure, childLine, "^");
        obj[key] = childObj;
      }
      else if (fieldDef.Type == JTokenType.Object)
      {
        // nested object
        string childLine = index < tokens.Length ? tokens[index] : "";
        index++;

        JObject childObj = ParseContentText(new JArray(fieldDef), childLine, "^");
        obj[key] = childObj;
      }
    }

    return obj;
  }
  private static JArray ParseResultText(JArray structure, List<string> lines, string sep = "|")
  {
    var arr = new JArray();
    foreach (var line in lines)
    {
      arr.Add(ParseContentText(structure, line, sep));
    }
    return arr;
  }
  private static string ConvertJSONToText(JObject structure, JObject data)
  {
    List<string> lines = new();

    // Get Header line as first line
    JArray headerStructure = (JArray)structure["Header"]!["fields"]!;
    JObject dataHeader = (JObject)data["Header"]!;
    lines.Add(GetHeaderText(headerStructure, dataHeader));

    // Patient data
    JArray patientStructure = (JArray)structure["Patient"]!["fields"]!;
    JObject dataPatient = (JObject)data["Patient"]!;
    lines.Add(GetContentText(patientStructure, dataPatient, sequence: "1"));

    // Order data
    JArray orderStructure = (JArray)structure["Order"]!["fields"]!;
    JObject dataOrder = (JObject)data["Order"]!;
    lines.Add(GetContentText(orderStructure, dataOrder, sequence: "1"));

    // Result data
    JArray resultStructure = (JArray)structure["Result"]!["fields"]!;
    JArray resultList = (JArray)data["Result"]!;


    foreach (var (item, index) in resultList.Select((item, index) => (item, index)))
    {
      lines.Add(GetContentText(resultStructure, item, sequence: (index + 1).ToString()));
    }

    // Termination data
    JArray terminationStructure = (JArray)structure["Termination"]!["fields"]!;
    JObject dataTermination = (JObject)data["Termination"]!;
    lines.Add(GetContentText(terminationStructure, dataTermination, sequence: "1"));

    return string.Join("\n", lines);
  }
  public static void Main(string[] args)
  {

    // Load env variable
    Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning) // Reduce framework noise
    .WriteTo.Console()
    .WriteTo.File("logs/myapp-.txt", rollingInterval: RollingInterval.Day) // Log to a daily rotating file
    .CreateLogger();

    Env.Load();
    string portName = Environment.GetEnvironmentVariable("SERIAL_PORT") ?? "/dev/pts/1";
    int baudRate = int.Parse(Environment.GetEnvironmentVariable("BAUD_RATE") ?? "9600");
    int dataBits = int.Parse(Environment.GetEnvironmentVariable("DATA_BITS") ?? "8");


    SerialPort serialPort = new SerialPort();
    serialPort.PortName = portName;
    serialPort.BaudRate = baudRate;
    serialPort.Parity = Parity.None;
    serialPort.DataBits = dataBits;
    serialPort.StopBits = StopBits.One;
    serialPort.Handshake = Handshake.None;


    serialPort.DataReceived += new SerialDataReceivedEventHandler(ReceiveHandler);
    Log.Information("Load Structure & Data From JSON File...");

    string structureString = File.ReadAllText("structure.json");
    dynamic? structure = null;

    string dataString = File.ReadAllText("data.json");
    dynamic? data = null;

    if (!string.IsNullOrEmpty(structureString))
    {
      structure = JsonConvert.DeserializeObject<dynamic>(structureString);
    }
    if (!string.IsNullOrEmpty(dataString))
    {
      data = JsonConvert.DeserializeObject<dynamic>(dataString);
    }
    if (data == null || structure == null)
    {
      Log.Error("Failed to load structure or data");
      return;
    }
    TEXT = ConvertJSONToText(new JObject(structure), new JObject(data![0]));
    //  new JObject(structure);
    // ConvertJSONToText(structure, data);
    // return;
    try
    {
      serialPort.Open();
      // Send ENQ to Server
      Console.WriteLine("Serial port opened.");
      serialPort.Write(new byte[] { ENQ }, 0, 1);
      Console.WriteLine("Send ENQ to Server.");

      while (running)
      {
        Thread.Sleep(100); // Wait for the program to finish sending all text
      }
      serialPort.Close();
    }
    catch (Exception e)
    {
      Console.WriteLine("Error: " + e.Message);
    }
  }
  private static void ReceiveHandler(object sender, SerialDataReceivedEventArgs e)
  {
    SerialPort sp = (SerialPort)sender;
    int data = sp.ReadByte();
    if (data == ACK)
    {
      Console.WriteLine("Received ACK from Server.");
      SendText(sp);

    }
    else if (data == NAK)
    {
      Console.WriteLine("Received NAK from Server.");
      // Resend the last text
      // partition = Math.Max(0, partition - 1);
      sequence = sequence == 0 ? MAX_SEQUENCE : sequence - 1;
      if (partition == 0 && line > 0)
      {
        line = Math.Max(0, line - 1);
      }
      partition = lastPartition;
      SendText(sp);
    }
    else
    {
      Console.WriteLine("Received unknown data from Server: " + data);
    }
  }
  private static string? GetText()
  {
    string[] lines = TEXT.Split('\n');
    if (line < lines.Length)
    {
      lastPartition = partition;
      string text;
      if (lines[line].Length <= maxCharactersPerLine)
      {
        text = lines[line];
        line++;
        partition = 0;
      }
      else
      {
        int totalPartition = (int)Math.Ceiling((double)lines[line].Length / maxCharactersPerLine);
        int startIndex = partition * maxCharactersPerLine;
        // split string each maxCharactersPerLine characters
        text = lines[line].Substring(startIndex, Math.Min(maxCharactersPerLine, lines[line].Length - startIndex));
        partition++;
        Console.WriteLine("Total partition: " + totalPartition);
        Console.WriteLine("Current partition: " + partition);
        if (partition >= totalPartition)
        {
          line++;
          partition = 0;
        }
      }
      return text;
    }
    return null;
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

    int total = contentBytes.Sum(c => (int)c);
    string checkSum = (total % 256).ToString("X2");
    return checkSum;
  }

  private static void SendText(SerialPort sp)
  {
    string? text = GetText();
    Console.WriteLine("Partition: " + partition + ", Sequence: " + sequence);

    if (text == null)
    {
      Console.WriteLine("All text sent.");
      // Send EOT to Server
      sp.Write(new byte[] { EOT }, 0, 1);
      Console.WriteLine("Send EOT to Server.");
      sp.Close();
      Reset();
      // Stop the program
      running = false;
      return;
    }

    List<byte> packet;
    byte[] content = Encoding.Latin1.GetBytes(text);
    string checkSum;
    bool simulationMode = Environment.GetEnvironmentVariable("SIMULATION_MODE") == "true";
    byte currentSequence = Encoding.Latin1.GetBytes(sequence.ToString())[0];
    byte endtx = partition > 0 ? ETB : ETX;
    if (simulationMode && new Random().Next(0, 2) == 0)
    {
      // simulate error by changing sequence number to 9
      // random booleancurrentSequence = 9;
      currentSequence = 9;
      Console.WriteLine("Simulation mode: sending wrong sequence number");
    }

    if (partition > 0)
    {
      // [STX][TEXT][ETB]
      packet = [STX, currentSequence, .. content, endtx];
      checkSum = GenerateCheckSum([.. packet], endtx);
    }
    else
    {
      // [STX][TEXT][CR][ETX]
      packet = [STX, currentSequence, .. content, CR, endtx];
      checkSum = GenerateCheckSum([.. packet], endtx);
    }

    if (simulationMode && new Random().Next(0, 2) == 0)
    {
      // simulate error by changing checksum to "FF"
      checkSum = "FF";
      Console.WriteLine("Simulation mode: sending wrong checksum");
    }

    // List<string> checkSumList = [checkSum[0].ToString(), checkSum[1].ToString()];
    // byte[] checkSumBytes = checkSumList.Select(s => (byte)s[0]).ToArray();

    byte[] checkSumBytes = Encoding.Latin1.GetBytes(checkSum);
    packet.AddRange([.. checkSumBytes, CR, LF]);
    Log.Information("Validating checksum before sending: " + IsValidChecksum(packet.ToArray(), endtx));

    sp.Write(packet.ToArray(), 0, packet.Count);
    LogPacket(packet.ToArray());
    Console.WriteLine("Sent text to Server: " + text.Trim());
    if (sequence < MAX_SEQUENCE)
    {
      sequence++;
    }
    else
    {
      sequence = 0;
    }
  }
}
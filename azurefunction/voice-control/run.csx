#r "System.Runtime.Serialization"

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Devices;
using NAudio.Wave;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using System.Linq;

public static void Run(Stream myBlob, string name, TraceWriter log)
{
    var connectionString = "HostName=devkit-luis-iot-hub.azure-devices.net;SharedAccessKeyName=iothubowner;SharedAccessKey=h8SKPsTaSyAUrvrPh5BjRifAjU9wXO4na2OGn29r6nE=";
    var cloudClient = ServiceClient.CreateFromConnectionString(connectionString);
    var speechClient = new SpeechClient("en", "en");
    var textDecoder = TextMessageDecoder.CreateTranslateDecoder();
    speechClient.OnTextData += (c, a) => { textDecoder.AppendData(a); };
    speechClient.OnEndOfTextData += (c, a) =>
    {
        textDecoder.AppendData(a);
        textDecoder.Decode().ContinueWith(t =>
        {
            var final = t.Result as FinalResultMessage;
            if (!t.IsFaulted && final != null)
            {
                log.Info("Translation: " + final.Translation);
                var command = ParseIntent(final.Translation, log);
                log.Info(command);
                
                cloudClient.SendAsync("devkit", new Message(Encoding.ASCII.GetBytes(command))).Wait();
                Task.Factory.StartNew(() => speechClient.Disconnect()).Wait();
            }
        });
    };

    speechClient.Connect().Wait();
    speechClient.SendMessage(new ArraySegment<byte>(GetWaveHeader())).Wait();
    using (MemoryStream ms = new MemoryStream())
    {
        myBlob.CopyTo(ms);
        var audioSource = new AudioSourceCollection(new IAudioSource[]
        {
            new WavFileAudioSource(ms.ToArray()),
            new WavSilenceAudioSource()
        });
        var handle = new AutoResetEvent(true);
        var audioChunkSizeInMs = 100;
        var audioChunkSizeInTicks = TimeSpan.TicksPerMillisecond * (long)(audioChunkSizeInMs);
        var tnext = DateTime.Now.Ticks + audioChunkSizeInMs;
        var wait = audioChunkSizeInMs;
        
        foreach (var chunk in audioSource.Emit(audioChunkSizeInMs))
        {
            speechClient.SendMessage(new ArraySegment<byte>(chunk.Array, chunk.Offset, chunk.Count)).Wait();
            handle.WaitOne(wait);
            tnext = tnext + audioChunkSizeInTicks;
            wait = (int)((tnext - DateTime.Now.Ticks) / TimeSpan.TicksPerMillisecond);
            if (wait < 0) wait = 0;
        }
        speechClient.ReceiveMessage().Wait();
    }
}

private static string ParseIntent(string text, TraceWriter log)
{
    string endPoint = "https://westus.api.cognitive.microsoft.com/luis/v2.0/apps/dfe75ca3-b119-46ed-bf5f-4a51f924037e?subscription-key=8d0ef6706f7d4d3296ee7e76b42b1fa8&timezoneOffset=0&verbose=true&q=";
    Dictionary<string, int> dic = new Dictionary<string, int>()
    {
        {"one",1},{"two",2},{"three",3},{"four",4},{"five",5},{"six",6},{"seven",7},{"eight",8},{"nine",9}
    };
    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endPoint + text);
    HttpWebResponse response = (HttpWebResponse)request.GetResponse();
    Stream resStream = response.GetResponseStream();
    StreamReader reader = new StreamReader(resStream);
    string strResponse = reader.ReadToEnd();
    dynamic result = JsonConvert.DeserializeObject(strResponse);
    if (result.topScoringIntent != null)
    {
        var intent = result.topScoringIntent.intent.ToString();
        Console.WriteLine("Intent:" + intent);
        log.Info("Intent:" + intent);
        if (intent == "SwitchLight")
        {
            if (result.entities.Count != 0)
            {
                var entity = result.entities[0].entity.ToString();
                if (entity == "off")
                {
                    Console.WriteLine("Command: Turn off the light");
                    log.Info("Command: Turn off the light");
                    return "light:off";
                }

                if (entity == "on")
                {
                    Console.WriteLine("Command: Turn on the light");
                    log.Info("Command: Turn on the light");
                    return "light:on";
                }
            }
            Console.WriteLine("Cannot parse this command");
            log.Info("Cannot parse this command");
            return "None";

        }
        if (intent == "Blink")
        {
            if (result.entities.Count != 0)
            {
                var entity = result.entities[0].entity.ToString();
                int num;
                if (int.TryParse(entity, out num))
                {
                    Console.WriteLine($"Blink the light {num} times");
                    return "blink:" + num;
                }
                else
                {
                    try
                    {
                        return "blink:" + dic[entity];
                    }
                    catch (Exception)
                    {
                        //ignore
                    }
                }
            }

            Console.WriteLine("Cannot parse Blink intent");
            return "None";
        }
        if (intent == "Display")
        {
            string str = result.query.ToString();
            str = Regex.Replace(str, "display ", "", RegexOptions.IgnoreCase);
            Console.WriteLine("Display:" + str);
            if (str.Length > 50)
            {
                str = str.Substring(0, 50);
            }
            return "display:" + str;
        }
        if (intent == "Sensor")
        {
            if (result.entities.Count != 0)
            {
                var entity = result.entities[0].entity.ToString();
                if (entity == "temperature" || entity == "humidity")
                {
                    return "sensor:humidtemp";
                }
                if (entity == "motion")
                {
                    return "sensor:motiongyro";
                }
                if (entity == "magnetic")
                {
                    return "sensor:magnetic";
                }
                if (entity == "pressure")
                {
                    return "sensor:pressure";
                }
                Console.WriteLine("Cannot parse sensor:" + entity);
                return "None";
            }

            Console.WriteLine("Cannot parse Sensor intent");
            return "None";
        }
    }
    Console.WriteLine("Cannot parse user intent");
    return "None";
}



private static byte[] GetWaveHeader()
{
    var waveFormat = new WaveFormat(8000, 16, 1);
    using (var stream = new MemoryStream())
    {
        var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(Encoding.UTF8.GetBytes("RIFF"));
        writer.Write(0);
        writer.Write(Encoding.UTF8.GetBytes("WAVE"));
        writer.Write(Encoding.UTF8.GetBytes("fmt "));
        waveFormat.Serialize(writer);
        writer.Write(Encoding.UTF8.GetBytes("data"));
        writer.Write(0);
        stream.Position = 0;
        var buffer = new byte[stream.Length];
        stream.Read(buffer, 0, buffer.Length);
        return buffer;
    }
}

public class AzureAuthToken
{
    private static readonly Uri ServiceUrl = new Uri("https://api.cognitive.microsoft.com/sts/v1.0/issueToken");
    private const string OcpApimSubscriptionKeyHeader = "Ocp-Apim-Subscription-Key";
    /// actual token lifetime of 10 minutes, use a duration of 5 minutes
    private static readonly TimeSpan TokenCacheDuration = new TimeSpan(0, 5, 0);
    private string _storedTokenValue = string.Empty;
    private DateTime _storedTokenTime = DateTime.MinValue;
    public string SubscriptionKey { get; private set; }
    public HttpStatusCode RequestStatusCode { get; private set; }

    public AzureAuthToken(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentNullException(nameof(key), "A subscription key is required");
        }

        this.SubscriptionKey = key;
        this.RequestStatusCode = HttpStatusCode.InternalServerError;
    }
    public async Task<string> GetAccessTokenAsync()
    {
        if ((DateTime.Now - _storedTokenTime) < TokenCacheDuration)
        {
            return _storedTokenValue;
        }

        using (var client = new HttpClient())
        using (var request = new HttpRequestMessage())
        {
            request.Method = HttpMethod.Post;
            request.RequestUri = ServiceUrl;
            request.Content = new StringContent(string.Empty);
            request.Headers.TryAddWithoutValidation(OcpApimSubscriptionKeyHeader, this.SubscriptionKey);
            var response = await client.SendAsync(request);
            this.RequestStatusCode = response.StatusCode;
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadAsStringAsync();
            _storedTokenTime = DateTime.Now;
            _storedTokenValue = "Bearer " + token;
            return _storedTokenValue;
        }
    }
}

public class SpeechClient
{
    private readonly ClientWebSocket _webSocketClient;
    private readonly Uri _clientWsUri;
    private const string HostName = "dev.microsofttranslator.com";
    private const int ReceiveChunkSize = 8 * 1024;
    private const string SubscriptionKey = "7d09a0cc88ae469cbb0073472e756f98";
    public event EventHandler<ArraySegment<byte>> OnTextData;
    public event EventHandler<ArraySegment<byte>> OnEndOfTextData;
    
    public SpeechClient(string source, string target)
    {
        var auth = new AzureAuthToken(SubscriptionKey);
        this._webSocketClient = new ClientWebSocket();
        _webSocketClient.Options.SetRequestHeader("Authorization", auth.GetAccessTokenAsync().Result);
        _webSocketClient.Options.SetRequestHeader("X-ClientAppId", "ea66703d-90a8-436b-9bd6-7a2707a2ad99");
        _webSocketClient.Options.SetRequestHeader("X-CorrelationId", "440B2DA4");
        this._clientWsUri = new Uri($"wss://{HostName}/speech/translate?from={source}&to={target}&features=Partial&profanity=Strict&api-version=1.0");
    }

    public async Task Connect()
    {
        await _webSocketClient.ConnectAsync(_clientWsUri, CancellationToken.None);
    }

    public bool IsConnected()
    {
        WebSocketState wsState;
        try
        {
            wsState = _webSocketClient.State;
        }
        catch (ObjectDisposedException)
        {
            wsState = WebSocketState.None;
        }
        return wsState == WebSocketState.Open || wsState == WebSocketState.CloseReceived;
    }

    public async Task Disconnect()
    {
        if (IsConnected())
        {
            await _webSocketClient.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
        }
    }

    public async Task SendMessage(ArraySegment<byte> content)
    {
       await _webSocketClient.SendAsync(content, WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    public async Task ReceiveMessage()
    {
        var buffer = new byte[ReceiveChunkSize];
        var arraySegmentBuffer = new ArraySegment<byte>(buffer);
        Task<WebSocketReceiveResult> receiveTask = null;
        var disconnecting = false;
        while (IsConnected() && !disconnecting)
        {
            if (receiveTask == null)
            {
                receiveTask = _webSocketClient.ReceiveAsync(arraySegmentBuffer, CancellationToken.None);
            }
            if (receiveTask.Wait(100))
            {
                var result = await receiveTask;
                receiveTask = null;
                EventHandler<ArraySegment<byte>> handler = null;
                switch (result.MessageType)
                {
                    case WebSocketMessageType.Close:
                        disconnecting = true;
                        await this.Disconnect();
                        break;
                    case WebSocketMessageType.Text:
                        handler = result.EndOfMessage ? this.OnEndOfTextData : this.OnTextData;
                        break;
                }
                if (handler != null)
                {
                    var data = new byte[result.Count];
                    Array.Copy(buffer, data, result.Count);
                    handler(this, new ArraySegment<byte>(data));
                }
            }
        }
    }
}

public class TextMessageDecoder
{
    private MemoryStream buffer;
    private readonly Dictionary<string, Type> _resultTypeMap;

    public static TextMessageDecoder CreateTranslateDecoder()
    {
        return new TextMessageDecoder(new Dictionary<string, Type>
        {
            {"final", typeof(FinalResultMessage)},
            {"partial", typeof(PartialResultMessage)}
        });
    }

    private TextMessageDecoder(Dictionary<string, Type> mapper)
    {
        this._resultTypeMap = mapper;
        this.buffer = new MemoryStream();
    }

    public void AppendData(ArraySegment<byte> data)
    {
        buffer.Write(data.Array, data.Offset, data.Count);
    }

    public Task<object> Decode()
    {
        var ms = Interlocked.Exchange(ref this.buffer, new MemoryStream());
        ms.Position = 0;
        return Task.Run(() => {
            object msg = null;
            using (var reader = new StreamReader(ms, Encoding.UTF8))
            {
                var json = reader.ReadToEnd();
                var result = JsonConvert.DeserializeObject<ResultType>(json);
                var msgType = result.MessageType.ToLower();
                if (msgType != "final" && msgType !="partial")
                {
                    throw new InvalidOperationException($"Invalid text message: type='{msgType}'.");
                }
                msg = JsonConvert.DeserializeObject(json, this._resultTypeMap[msgType]);
            }
            return msg;
        });
    }
}

[DataContract]
public class ResultType
{
    [DataMember(Name = "type")]
    public string MessageType { get; set; }
}

[DataContract]
public class PartialResultMessage
{
    [DataMember(Name = "type")]
    public string Type = "partial";

    [DataMember(Name = "recognition")]
    public string Recognition;

    [DataMember(Name = "translation", EmitDefaultValue = false)]
    public string Translation;
}

[DataContract]
public class FinalResultMessage
{
    [DataMember(Name = "type")]
    public string Type = "final";

    [DataMember(Name = "recognition")]
    public string Recognition;

    [DataMember(Name = "translation", EmitDefaultValue = false)]
    public string Translation;
}

public interface IAudioSource
{
    IEnumerable<ArraySegment<byte>> Emit(int chunkDurationInMs);
}

public class WavFileAudioSource : IAudioSource
{
    private readonly byte[] _data;

    public WavFileAudioSource(byte[] byteArray, bool dataOnly = true)
    {
        this._data = byteArray;
        if (!dataOnly) return;

        using (var stream = new MemoryStream())
        {
            var chunkType = BitConverter.ToInt32(this._data, 0);
            var riffType = BitConverter.ToInt32(this._data, 8);
            if (chunkType != 0x46464952 || riffType != 0x45564157)
            {
                throw new InvalidDataException("Invalid WAV file");
            }

            var chunkStartIndex = 12;
            while (chunkStartIndex < (BitConverter.ToUInt32(this._data, 4) - 8))
            {
                chunkType = BitConverter.ToInt32(this._data, chunkStartIndex);
                var chunkSize = (int)BitConverter.ToUInt32(this._data, chunkStartIndex + 4);
                if (chunkType == 0x61746164)
                {
                    stream.Write(this._data, chunkStartIndex + 8, chunkSize - 8);
                }
                chunkStartIndex += 8 + chunkSize;
            }
            this._data = stream.ToArray();
        }
    }
    public IEnumerable<ArraySegment<byte>> Emit(int chunkDurationInMs)
    {
        if (chunkDurationInMs < 10 || chunkDurationInMs % 10 != 0)
        {
            throw new ArgumentException("chunkDurationInMs must be a factor of 10");
        }

        var bytesPerChunk = 320 * chunkDurationInMs / 10;
        var position = 0;
        var bytesRemaining = this._data.Length;
        while (bytesRemaining >= bytesPerChunk)
        {
            yield return new ArraySegment<byte>(this._data, position, bytesPerChunk);
            bytesRemaining -= bytesPerChunk;
            position += bytesPerChunk;
        }
        if (bytesRemaining > 0)
        {
            var buffer = new byte[bytesPerChunk];
            Buffer.BlockCopy(this._data, position, buffer, 0, bytesRemaining);
            yield return new ArraySegment<byte>(buffer, 0, bytesPerChunk);
        }
    }
}
/// Audio source generating silence matching WAV 16bit PCM 16kHz - 320 bytes / 10ms
public class WavSilenceAudioSource : IAudioSource
{
    public int DurationInMs { get; set; }

    public WavSilenceAudioSource(int durationInMs = 2000)
    {
        if (durationInMs < 10 || durationInMs % 10 != 0)
        {
            throw new ArgumentException("durationInMs must be a factor of 10");
        }
        this.DurationInMs = durationInMs;
    }

    public IEnumerable<ArraySegment<byte>> Emit(int chunkDurationInMs)
    {
        var bytesPerChunk = 320 * chunkDurationInMs / 10;
        var data = new byte[bytesPerChunk];
        var timeRemainingInMs = this.DurationInMs;
        while (timeRemainingInMs >= 0)
        {
            yield return new ArraySegment<byte>(data, 0, bytesPerChunk);
            timeRemainingInMs -= chunkDurationInMs;
        }
    }
}
public class AudioSourceCollection : IAudioSource
{
    public event EventHandler<IAudioSource> OnNewSourceDataEmit;
    private IEnumerable<IAudioSource> Sources;

    public AudioSourceCollection(IEnumerable<IAudioSource> sources)
    {
        this.Sources = sources;
    }

    public IEnumerable<ArraySegment<byte>> Emit(int chunkDurationInMs)
    {
        foreach (var source in this.Sources)
        {
            OnNewSourceDataEmit?.Invoke(this, source);
            foreach (var chunk in source.Emit(chunkDurationInMs))
            {
                yield return chunk;
            }
        }
    }
}
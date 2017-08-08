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
using System.Configuration;

private static Dictionary<string, int> numDic = new Dictionary<string, int>()
{
    {"one", 1}, {"two", 2}, {"three", 3}, {"four", 4}, {"five", 5}, {"six", 6}, {"seven", 7}, {"eight", 8}, {"nine", 9}
};

private static Dictionary<string, string> sensorDic = new Dictionary<string, string>(){
	{"temperature", "sensor:humidtemp"}, {"humidity", "sensor:humidtemp"}, {"motion", "sensor:motiongyro"},
	{"magnetic", "sensor:magnetic"}, {"pressure", "sensor:pressure"}
};

private static Dictionary<string, string> lightDic = new Dictionary<string, string>(){
	{"on", "light:on"}, {"off", "light:off"}
};


public static async Task<HttpResponseMessage> Run(HttpRequestMessage req, TraceWriter log)
{
    byte[] data;
    try
    {
        data = await req.Content.ReadAsByteArrayAsync();
        if (data.Length == 0)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest, "Not a valid sound");
        }
    }
    catch (Exception ex)
    {
        return req.CreateResponse(HttpStatusCode.BadRequest, ex.Message);
    }

    var connectionString = ConfigurationManager.AppSettings["iotHubConnectionString"];
    var cloudClient = ServiceClient.CreateFromConnectionString(connectionString);

    var speechClient = new SpeechClient();
    var textDecoder = TextMessageDecoder.CreateTranslateDecoder();

    speechClient.OnTextData += (c, a) =>
    {
        textDecoder.AppendData(a);
    };

    speechClient.OnEndOfTextData += (c, a) =>
    {
        textDecoder.AppendData(a);

        textDecoder.Decode().ContinueWith(async t =>
       {
           var final = t.Result as FinalResultMessage;

           if (!t.IsFaulted && final != null)
           {
               log.Info("Translation result: " + final.Translation);
               var intent = ParseIntent(final.Translation, log);
               log.Info(intent);
               var c2dTask = Task.Factory.StartNew(() => cloudClient.SendAsync("AZ3166", new Message(Encoding.ASCII.GetBytes(intent))));
               var disconnectTask = Task.Factory.StartNew(speechClient.Disconnect);
               await c2dTask;
               await disconnectTask;
            }
       });
    };

    await speechClient.Connect();
    await speechClient.SendMessage(new ArraySegment<byte>(GetWaveHeader()));

    var audioSource = new AudioSourceCollection(new IAudioSource[]
    {
        new WavFileAudioSource(data.ToArray()),
        new WavSilenceAudioSource()
    });

    var handle = new AutoResetEvent(true);
    var audioChunkSizeInMs = 100;
    var audioChunkSizeInTicks = TimeSpan.TicksPerMillisecond * (long)(audioChunkSizeInMs);
    var tnext = DateTime.Now.Ticks + audioChunkSizeInMs;
    var wait = audioChunkSizeInMs;

    foreach (var chunk in audioSource.Emit(audioChunkSizeInMs))
    {
        await speechClient.SendMessage(new ArraySegment<byte>(chunk.Array, chunk.Offset, chunk.Count));
        handle.WaitOne(wait);
        tnext = tnext + audioChunkSizeInTicks;
        wait = (int)((tnext - DateTime.Now.Ticks) / TimeSpan.TicksPerMillisecond);
        if (wait < 0) wait = 0;
    }

    await speechClient.ReceiveMessage();

    return req.CreateResponse(HttpStatusCode.OK, "OK");
}

private static string ParseIntent(string text, TraceWriter log)
{
    string endPoint = $"https://westus.api.cognitive.microsoft.com/luis/v2.0/apps/{ConfigurationManager.AppSettings["LuisAppId"]}"
                        + $"?subscription-key={ConfigurationManager.AppSettings["cognitiveIntentKey"]}&timezoneOffset=0&verbose=true&q=";

    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endPoint + text);
    HttpWebResponse response = (HttpWebResponse)request.GetResponse();
    Stream stream = response.GetResponseStream();
    StreamReader reader = new StreamReader(stream);
    dynamic result = JsonConvert.DeserializeObject(reader.ReadToEnd());

    if (result.topScoringIntent != null)
    {
        var intent = result.topScoringIntent.intent.ToString();

        switch (intent)
        {
            case "SwitchLight":
                if (result.entities.Count != 0)
                {
                    var entity = result.entities[0].entity.ToString();
					if(lightDic.ContainsKey(entity))
					{
						return lightDic[entity];
					}
                }
                log.Info("Cannot parse switch light intent");
                return "None";

            case "Blink":
                if (result.entities.Count != 0)
                {
                    var entity = result.entities[0].entity.ToString();
                    int num;
                    if (int.TryParse(entity, out num))
                    {
                        return "blink:" + num;
                    }
                    else
                    {
						if(numDic.ContainsKey(entity))
						{
							return "blink:" + numDic[entity];
						}
                        log.Info("Cannot parse blink times");
                    }
                }
                return "None";

            case "Display":
                var str = result.query.ToString();
                str = Regex.Replace(str, "display ", "", RegexOptions.IgnoreCase);

                if (str.Length > 50)
                {
                    str = str.Substring(0, 50);
                }

                return "display:" + str;

            case "Sensor":
                if (result.entities.Count != 0)
                {
                    var entity = result.entities[0].entity.ToString();
                    log.Info("Sensor: " + entity);
					if (sensorDic.ContainsKey(entity))
					{
						return sensorDic[entity];
					}
                    else
                    {
                        log.Info("Cannot parse sensor intent");
                        return "None";
                    }
                }
                return "None";
        }
    }
    log.Info("Cannot parse user intent");
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
    private string SubscriptionKey = ConfigurationManager.AppSettings["cognitiveSpeechKey"];
    public event EventHandler<ArraySegment<byte>> OnTextData;
    public event EventHandler<ArraySegment<byte>> OnEndOfTextData;

    public SpeechClient()
    {
        var auth = new AzureAuthToken(SubscriptionKey);
        this._webSocketClient = new ClientWebSocket();
        _webSocketClient.Options.SetRequestHeader("Authorization", auth.GetAccessTokenAsync().Result);
        _webSocketClient.Options.SetRequestHeader("X-ClientAppId", "ea66703d-90a8-436b-9bd6-7a2707a2ad99");
        _webSocketClient.Options.SetRequestHeader("X-CorrelationId", "440B2DA4");
        this._clientWsUri = new Uri($"wss://{HostName}/speech/translate?from=en&to=en&features=Partial&profanity=Strict&api-version=1.0");
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
        return Task.Run(() =>
        {
            object msg = null;
            using (var reader = new StreamReader(ms, Encoding.UTF8))
            {
                var json = reader.ReadToEnd();
                var result = JsonConvert.DeserializeObject<ResultType>(json);
                var msgType = result.MessageType.ToLower();
                if (msgType != "final" && msgType != "partial")
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

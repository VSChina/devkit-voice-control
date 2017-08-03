#include "Arduino.h"
#include "iot_client.h"
#include "AZ3166WiFi.h"
#include "OLEDDisplay.h"
#include "http_client.h"
#include "mbed_memory_status.h"
#include "json.h"
#include "AudioClass.h"
#include "Sensor.h"

static boolean hasWifi = false;
static const int recordedDuration = 3;
static char *waveFile = NULL;
static int wavFileSize;
static bool translated = false;
static const uint32_t delayTimes = 1000;
static AudioClass Audio;
static const int AUDIO_SIZE = ((32000 * recordedDuration) + 44);
static const char *DeviceConnectionString = "HostName=devkit-luis-iot-hub.azure-devices.net;DeviceId=devkit;SharedAccessKey=ubTyTDivvtuwJ/dEMGA1FdKVN8wbGRRW9UV6vo+b2HQ=";

enum STATUS
{
    Idle,
    Recorded,
    WavReady,
    Uploading,
    Uploaded
};
static STATUS status;

static struct _tagRGB
{
  int red;
  int green;
  int blue;
} _rgb[] =
{
  { 255,   0,   0 },
  {   0, 255,   0 },
  {   0,   0, 255 },
};

static RGB_LED rgbLed;

DevI2C *ext_i2c;
LSM6DSLSensor *acc_gyro;
int axes[3];
LPS22HBSensor *pressureSensor;
HTS221Sensor *ht_sensor;
LIS2MDLSensor *magnetometer;
int keepUpdate = 0;

/* float to string
 * f is the float to turn into a string
 * p is the precision (number of decimals)
 * return a string representation of the float.
 */
char *f2s(float f, int p){
  char * pBuff;                         // use to remember which part of the buffer to use for dtostrf
  const int iSize = 10;                 // number of bufffers, one for each float before wrapping around
  static char sBuff[iSize][20];         // space for 20 characters including NULL terminator for each float
  static int iCount = 0;                // keep a tab of next place in sBuff to use
  pBuff = sBuff[iCount];                // use this buffer
  if(iCount >= iSize -1){               // check for wrap
    iCount = 0;                         // if wrapping start again and reset
  }
  else{
    iCount++;                           // advance the counter
  }
  return dtostrf(f, 0, p, pBuff);       // call the library function
}

/*
 * As there is a problem of sprintf %f in Arduino,
   follow https://github.com/blynkkk/blynk-library/issues/14 to implement dtostrf
 */
char * dtostrf(double number, signed char width, unsigned char prec, char *s) {
    if(isnan(number)) {
        strcpy(s, "nan");
        return s;
    }
    if(isinf(number)) {
        strcpy(s, "inf");
        return s;
    }

    if(number > 4294967040.0 || number < -4294967040.0) {
        strcpy(s, "ovf");
        return s;
    }
    char* out = s;
    // Handle negative numbers
    if(number < 0.0) {
        *out = '-';
        ++out;
        number = -number;
    }
    // Round correctly so that print(1.999, 2) prints as "2.00"
    double rounding = 0.5;
    for(int i = 0; i < prec; ++i)
        rounding /= 10.0;
    number += rounding;

    // Extract the integer part of the number and print it
    unsigned long int_part = (unsigned long) number;
    double remainder = number - (double) int_part;
    out += sprintf(out, "%d", int_part);

    // Print the decimal point, but only if there are digits beyond
    if(prec > 0) {
        *out = '.';
        ++out;
    }

    while(prec-- > 0) {
        remainder *= 10.0;
        if((int)remainder == 0){
                *out = '0';
                 ++out;
        }
    }
    sprintf(out, "%d", (int) remainder);
    return s;
}

static void InitWiFi()
{
    if (WiFi.begin() == WL_CONNECTED)
    {
        hasWifi = true;
    }
    else
    {
        Screen.print(1, "No Wi-Fi");
    }
}

static void EnterIdleState()
{
    status = Idle;
    Screen.clean();
    Screen.print(0, "Hold B to talk");
}

void setup()
{
    Screen.clean();
    Screen.print(2, "Initializing...");
    Screen.print(3, " > WiFi");

    ext_i2c = new DevI2C(D14, D15);
  acc_gyro = new LSM6DSLSensor(*ext_i2c, D4, D5);
  acc_gyro->init(NULL);
  acc_gyro->enableAccelerator();
  acc_gyro->enableGyroscope();

  ht_sensor = new HTS221Sensor(*ext_i2c);
  ht_sensor->init(NULL);

  magnetometer = new LIS2MDLSensor(*ext_i2c);
  magnetometer->init(NULL);

  pressureSensor = new LPS22HBSensor(*ext_i2c);
  pressureSensor -> init(NULL);

    hasWifi = false;
    InitWiFi();
    EnterIdleState();
    iot_client_set_connection_string(DeviceConnectionString);
}

void log_time()
{
    time_t t = time(NULL);
    Serial.printf("Time is now (UTC): %s\r\n", ctime(&t));
}

void freeWavFile()
{
    if (waveFile != NULL)
    {
        free(waveFile);
        waveFile = NULL;
    }
}

void display(const char * text)
{
    Screen.clean();
    Screen.print(0,"Message");
    Screen.print(1,text,true);
}

void blinkLED(int times)
{
    for(int i = 0;i<times;i++) {
        rgbLed.setColor(_rgb[0].red, _rgb[0].green, _rgb[0].blue);
        delay(300);
        rgbLed.turnOff();
        delay(300);
    }
}

void switchLED(bool on)
{
    if(on) {
        rgbLed.setColor(_rgb[1].red, _rgb[1].green, _rgb[1].blue);
    }else {
        rgbLed.turnOff();
    }
}

void showMotionGyroSensor()
{
  acc_gyro->getXAxes(axes);
  char buff[128];
  sprintf(buff, "Gyroscope \r\n    x:%d   \r\n    y:%d   \r\n    z:%d  ", axes[0], axes[1], axes[2]);
  Screen.print(buff);
}

void showPressureSensor()
{
  float pressure = 0;
  float temperature = 0;
  pressureSensor -> getPressure(&pressure);
  pressureSensor -> getTemperature(&temperature);
  char buff[128];
  sprintf(buff, "Environment\r\nPressure: \r\n    %shPa\r\nTemp: %sC \r\n",f2s(pressure, 2), f2s(temperature, 1));
  Screen.print(buff);
}

void update(){
    switch(keepUpdate) {
        case 1: showMotionGyroSensor();break;
        case 2: showPressureSensor();break;
        case 3: showHumidTempSensor();break;
        // case 4: showMagneticSensor();break;
    }
}

void showHumidTempSensor()
{
  ht_sensor->reset();
  float temperature = 0;
  ht_sensor->getTemperature(&temperature);
  //convert from C to F
  temperature = temperature*1.8 + 32;
  float humidity = 0;
  ht_sensor->getHumidity(&humidity);

  char buff[128];
  sprintf(buff, "Environment \r\n Temp:%sF    \r\n Humidity:%s%% \r\n          \r\n",f2s(temperature, 1), f2s(humidity, 1));
  Screen.print(buff);
}

void showMagneticSensor()
{
  magnetometer->getMAxes(axes);
  char buff[128];
  sprintf(buff, "Magnetometer  \r\    x:%d     \r\n    y:%d     \r\n    z:%d     ", axes[0], axes[1], axes[2]);
  Screen.print(buff);
}

void loop()
{
    if (!hasWifi)
    {
        return;
    }



    uint32_t curr = millis();
    switch (status)
    {
    case Idle:
        if (digitalRead(USER_BUTTON_B) == LOW)
        {
            waveFile = (char *)malloc(AUDIO_SIZE + 1);
            if (waveFile == NULL)
            {
                Serial.println("No enough Memory! ");
                EnterIdleState();
                return;
            }
            memset(waveFile, 0, AUDIO_SIZE + 1);
            Audio.format(8000, 16);
            Audio.startRecord(waveFile, AUDIO_SIZE, recordedDuration);
            status = Recorded;
            Screen.clean();
            Screen.print(0, "Release B to send\r\nMax duraion: 3 sec");
        }
        update();
        break;
    case Recorded:
        if (digitalRead(USER_BUTTON_B) == HIGH)
        {
            Audio.getWav(&wavFileSize);
            if (wavFileSize > 0)
            {
                wavFileSize = Audio.convertToMono(waveFile, wavFileSize, 16);
                if (wavFileSize <= 0)
                {
                    Serial.println("ConvertToMono failed! ");
                    EnterIdleState();
                    freeWavFile();
                }
                else
                {
                    status = WavReady;
                    Screen.clean();
                    Screen.print(0, "Processing...          ");
                    Screen.print(1, "Uploading #1", true);
                }
            }
            else
            {
                Serial.println("No Data Recorded! ");
                freeWavFile();
                EnterIdleState();
            }
        }
        break;
    case WavReady:
        if (wavFileSize > 0 && waveFile != NULL)
        {
            Serial.print("begin uploading: ");
            log_time();
            if (0 == iot_client_blob_upload_step1("test.wav"))
            {
                status = Uploading;
                Screen.clean();
                Screen.print(0, "Processing...");
                Screen.print(1, "Uploading audio...");
            }
            else
            {
                Serial.println("Upload step 1 failed");
                freeWavFile();
                EnterIdleState();
            }
        }
        else
        {
            freeWavFile();
            EnterIdleState();
        }
        break;
    case Uploading:
        if (iot_client_blob_upload_step2(waveFile, wavFileSize) == 0)
        {
            status = Uploaded;
            Serial.print("uploaded: ");
            log_time();
            Screen.clean();
            Screen.print(0, "Processing...          ");
            Screen.print(1, "Receiving...", true);
        }
        else
        {
            freeWavFile();
            EnterIdleState();
        }
        break;
    case Uploaded:
    Serial.println("start get c2d");
        char* etag = (char*)malloc(40);
        Serial.println(etag);
        const char *p = iot_client_get_c2d_message(etag);
        Serial.println(etag);
        Serial.print("finish get c2d\n");
        if (p != NULL)
        {
            Serial.print("start delete c2d\n");
            complete_c2d_message((char *)etag);
            Serial.print("finish delete c2d\n");
            if (strlen(p) != 0) {
                Serial.printf("received %s\n",p);
                if(strncmp(p,"display:",8) == 0) {
                    display(p+8);
                    keepUpdate = 0;
                }else if(strncmp(p,"blink:",6) == 0) {
                    int times = atoi((char*)p+6);
                    keepUpdate = 0;
                    Screen.clean();
                    blinkLED(times);
                }else if(strncmp(p,"light:on",8) == 0) {
                    keepUpdate = 0;
                    Screen.clean();
                    switchLED(true);
                }else if(strncmp(p,"light:off",9) == 0) {
                    keepUpdate = 0;
                    Screen.clean();
                    switchLED(false);
                }else if(strncmp(p,"sensor:motiongyro",17) == 0) {
                    keepUpdate = 1;
                    showMotionGyroSensor();
                }else if(strncmp(p,"sensor:pressure",15) == 0) {
                    keepUpdate = 2;
                    showPressureSensor();
                }else if(strncmp(p,"sensor:humidtemp",16) == 0) {
                    keepUpdate = 3;
                    showHumidTempSensor();
                }else if(strncmp(p,"sensor:magnetic",15) == 0) {
                    showMagneticSensor();
                    keepUpdate = 4;
                }else if(strncmp(p,"None",4) == 0) {
                    display("Invalid command");
                    keepUpdate = 0;
                }
                log_time();
                translated = true;
                status = Idle;
                // Screen.print(0, "Hold B to talk");
            }
                free((void *)p);

        }
        if(etag!=NULL) {
            free((void *)etag);
        }

        freeWavFile();

        break;
    }

    curr = millis() - curr;
    if (curr < delayTimes)
    {
        delay(delayTimes - curr);
    }
}

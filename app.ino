#include "Arduino.h"
#include "AudioClass.h"
#include "AZ3166WiFi.h"
#include "EEPROMInterface.h"
#include "http_client.h"
#include "json.h"
#include "OLEDDisplay.h"
#include "Sensor.h"

#include "iot_client.h"
#include "azure_config.h"

static const int RECORD_DURATION = 3;
static const int AUDIO_SIZE = ((32000 * RECORD_DURATION) + 44);
static const uint32_t DELAY_TIME = 1000;
static const char* FUNCTION_URL = "http://%s.azurewebsites.net/api/voice-control";

static boolean hasWifi = false;
static char *waveFile = NULL;
static int wavFileSize;
static AudioClass& Audio = AudioClass::getInstance();
static char azureFunctionUri[128];

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
        {255, 0, 0},
        {0, 255, 0},
        {0, 0, 255},
};

static RGB_LED rgbLed;

DevI2C *ext_i2c;
LSM6DSLSensor *acc_gyro;
int axes[3];
LPS22HBSensor *pressureSensor;
HTS221Sensor *ht_sensor;
LIS2MDLSensor *magnetometer;
int updateSensorIndicator = 0;

/* float to string
 * f is the float to turn into a string
 * p is the precision (number of decimals)
 * return a string representation of the float.
 */
char *f2s(float f, int p)
{
    char *pBuff;                  // use to remember which part of the buffer to use for dtostrf
    const int iSize = 10;         // number of bufffers, one for each float before wrapping around
    static char sBuff[iSize][20]; // space for 20 characters including NULL terminator for each float
    static int iCount = 0;        // keep a tab of next place in sBuff to use
    pBuff = sBuff[iCount];        // use this buffer
    if (iCount >= iSize - 1)
    {               // check for wrap
        iCount = 0; // if wrapping start again and reset
    }
    else
    {
        iCount++; // advance the counter
    }
    return dtostrf(f, 0, p, pBuff); // call the library function
}

/*
 * As there is a problem of sprintf %f in Arduino,
   follow https://github.com/blynkkk/blynk-library/issues/14 to implement dtostrf
 */
char *dtostrf(double number, signed char width, unsigned char prec, char *s)
{
    if (isnan(number))
    {
        strcpy(s, "nan");
        return s;
    }
    if (isinf(number))
    {
        strcpy(s, "inf");
        return s;
    }

    if (number > 4294967040.0 || number < -4294967040.0)
    {
        strcpy(s, "ovf");
        return s;
    }
    char *out = s;
    // Handle negative numbers
    if (number < 0.0)
    {
        *out = '-';
        ++out;
        number = -number;
    }
    // Round correctly so that print(1.999, 2) prints as "2.00"
    double rounding = 0.5;
    for (int i = 0; i < prec; ++i)
        rounding /= 10.0;
    number += rounding;

    // Extract the integer part of the number and print it
    unsigned long int_part = (unsigned long)number;
    double remainder = number - (double)int_part;
    out += sprintf(out, "%d", int_part);

    // Print the decimal point, but only if there are digits beyond
    if (prec > 0)
    {
        *out = '.';
        ++out;
    }

    while (prec-- > 0)
    {
        remainder *= 10.0;
        if ((int)remainder == 0)
        {
            *out = '0';
            ++out;
        }
    }
    sprintf(out, "%d", (int)remainder);
    return s;
}

static void initWifi()
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

static void enterIdleState()
{
    status = Idle;
    Screen.clean();
    Screen.print(0, "Hold B to talk");
}

static int postToFunction(const char *content, int length)
{
    if (content == NULL || length <= 0 || length > MAX_UPLOAD_SIZE)
    {
        Serial.println("Content not valid");
        return -1;
    }
    Serial.printf("Function url:%s\n",azureFunctionUri);
    HTTPClient client = HTTPClient(HTTP_POST, azureFunctionUri);
    const Http_Response *response = client.send(content, length);
    Serial.println(response->status_code);
    if (response != NULL && response->status_code == 200)
    {
        return 0;
    }
    return -1;
}

void freeWavFile()
{
    if (waveFile != NULL)
    {
        free(waveFile);
        waveFile = NULL;
    }
}

void display(const char *text)
{
    Screen.clean();
    Screen.print(0, "Message");
    Screen.print(1, text, true);
}

void blinkLED(int times)
{
    for (int i = 0; i < times; i++)
    {
        rgbLed.setColor(_rgb[0].red, _rgb[0].green, _rgb[0].blue);
        delay(300);
        rgbLed.turnOff();
        delay(300);
    }
}

void switchLED(bool on)
{
    if (on)
    {
        rgbLed.setColor(_rgb[1].red, _rgb[1].green, _rgb[1].blue);
    }
    else
    {
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
    pressureSensor->getPressure(&pressure);
    pressureSensor->getTemperature(&temperature);
    char buff[128];
    sprintf(buff, "Environment\r\nPressure: \r\n    %shPa\r\nTemp: %sC \r\n", f2s(pressure, 2), f2s(temperature, 1));
    Screen.print(buff);
}

void showHumidTempSensor()
{
    ht_sensor->reset();
    float temperature = 0;
    ht_sensor->getTemperature(&temperature);
    //convert from C to F
    temperature = temperature * 1.8 + 32;
    float humidity = 0;
    ht_sensor->getHumidity(&humidity);

    char buff[128];
    sprintf(buff, "Environment \r\n Temp:%sF    \r\n Humidity:%s%% \r\n          \r\n", f2s(temperature, 1), f2s(humidity, 1));
    Screen.print(buff);
}

void showMagneticSensor()
{
    magnetometer->getMAxes(axes);
    char buff[128];
    sprintf(buff, "Magnetometer  \r\    x:%d     \r\n    y:%d     \r\n    z:%d     ", axes[0], axes[1], axes[2]);
    Screen.print(buff);
}

void updateSensor()
{
    switch (updateSensorIndicator)
    {
    case 1:
        showMotionGyroSensor();
        break;
    case 2:
        showPressureSensor();
        break;
    case 3:
        showHumidTempSensor();
        break;
        // case 4: showMagneticSensor();break;
    }
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
    pressureSensor->init(NULL);

    hasWifi = false;
    initWifi();
    enterIdleState();
    EEPROMInterface eeprom;
    uint8_t connString[AZ_IOT_HUB_MAX_LEN + 1] = {'\0'};

    if (eeprom.read(connString, AZ_IOT_HUB_MAX_LEN, 0x00, AZ_IOT_HUB_ZONE_IDX) < 0)
    {
        Serial.print("Iot Hub connection string is invalid");
    }
    iot_client_set_connection_string((const char *)connString);

    if (strlen(AZURE_FUNCTION_APP_NAME) == 0)
    {
        Screen.print(2, "Azure Resource Init Failed", true);
        return;
    }
    sprintf(azureFunctionUri, FUNCTION_URL, (char *)AZURE_FUNCTION_APP_NAME);
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
                enterIdleState();
                return;
            }
            memset(waveFile, 0, AUDIO_SIZE + 1);
            Audio.format(8000, 16);
            Audio.startRecord(waveFile, AUDIO_SIZE, RECORD_DURATION);
            status = Recorded;
            Screen.clean();
            Screen.print(0, "Release B to send\r\nMax duraion: 3 sec");
        }else {
            updateSensor();
        }
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
                    enterIdleState();
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
                enterIdleState();
            }
        }
        break;
    case WavReady:
        if (wavFileSize > 0 && waveFile != NULL)
        {
            Serial.print("begin uploading: ");
            if (postToFunction(waveFile, wavFileSize) == 0)
            {
                status = Uploaded;
                Screen.print(1, "Receiving...");
            }
            else
            {
                Serial.println("Error happened when translating");
                enterIdleState();
                Screen.print(2, "azure function failed", true);
            }
            
        }
        else
        {
            enterIdleState();
        }
        freeWavFile();
        break;
    case Uploaded:
        char *etag = (char *)malloc(40);
        const char *p = iot_client_get_c2d_message(etag);
        if (p != NULL)
        {
            complete_c2d_message((char *)etag);
            if (strlen(p) != 0)
            {
                Serial.printf("received %s\n", p);
                if (strncmp(p, "display:", 8) == 0)
                {
                    display(p + 8);
                    updateSensorIndicator = 0;
                }
                else if (strncmp(p, "blink:", 6) == 0)
                {
                    int times = atoi((char *)p + 6);
                    updateSensorIndicator = 0;
                    Screen.clean();
                    blinkLED(times);
                }
                else if (strncmp(p, "light:on", 8) == 0)
                {
                    updateSensorIndicator = 0;
                    Screen.clean();
                    switchLED(true);
                }
                else if (strncmp(p, "light:off", 9) == 0)
                {
                    updateSensorIndicator = 0;
                    Screen.clean();
                    switchLED(false);
                }
                else if (strncmp(p, "sensor:motiongyro", 17) == 0)
                {
                    updateSensorIndicator = 1;
                    showMotionGyroSensor();
                }
                else if (strncmp(p, "sensor:pressure", 15) == 0)
                {
                    updateSensorIndicator = 2;
                    showPressureSensor();
                }
                else if (strncmp(p, "sensor:humidtemp", 16) == 0)
                {
                    updateSensorIndicator = 3;
                    showHumidTempSensor();
                }
                else if (strncmp(p, "sensor:magnetic", 15) == 0)
                {
                    showMagneticSensor();
                    updateSensorIndicator = 4;
                }
                else if (strncmp(p, "None", 4) == 0)
                {
                    display("Invalid command");
                    updateSensorIndicator = 0;
                }
                enterIdleState();
                // Screen.print(0, "Hold B to talk");
            }
            free((void *)p);
        }
        if (etag != NULL)
        {
            free((void *)etag);
        }

        freeWavFile();

        break;
    }

    curr = millis() - curr;
    if (curr < DELAY_TIME)
    {
        delay(DELAY_TIME - curr);
    }
}

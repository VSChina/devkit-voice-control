// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#include "iot_client.h"
#include "Arduino.h"
#include <json.h>
#include <stdlib.h>
#include "azure_c_shared_utility/sastoken.h"
#include "http_client.h"

static char *hostNameString = NULL;
static char *deviceIdString = NULL;
static char *deviceKeyString = NULL;
static size_t current_expiry = 0;
static char *current_token = NULL;
static char *sasUri = NULL;
static char *correlationId = NULL;
static char temp[1024];
static char temp2[1024];

void set_string(char **p, const char *value, int length)
{
    if (*p != NULL)
    {
        free(*p);
    }
    *p = (char *)malloc(length + 1);
    strcpy(*p, value);
}

int get_sas_token()
{
    time_t currentTime = time(NULL);
    if (currentTime == (time_t)-1 || currentTime < 1492333149)
    {
        Serial.println("Time does not appear to be working.");
        return -1;
    }
    size_t expiry = (size_t)(difftime(currentTime, 0) + 3600);
    if (current_expiry > (size_t)(difftime(currentTime, 0)))
    {
        return 0;
    }
    current_expiry = expiry;
    STRING_HANDLE keyString = STRING_construct(deviceKeyString);
    sprintf(temp2, "%s/devices/%s", hostNameString, deviceIdString);
    STRING_HANDLE uriResource = STRING_construct(temp2);
    STRING_HANDLE empty = STRING_new();
    STRING_HANDLE newSASToken = SASToken_Create(keyString, uriResource, empty, expiry);
    set_string(&current_token, STRING_c_str(newSASToken), STRING_length(newSASToken));
    STRING_delete(newSASToken);
    STRING_delete(keyString);
    STRING_delete(uriResource);
    STRING_delete(empty);
    return 0;
}

int validate_iot()
{
    if (hostNameString == NULL || deviceIdString == NULL || deviceKeyString == NULL)
    {
        Serial.println("Iot hub connection string is not initialized");
        return -1;
    }

    if (get_sas_token() != 0)
    {
        Serial.println("Cannot generate sas token.");
        return -1;
    }
    return 0;
}

const char *get_json_string(json_object *obj, const char *name)
{
    return json_object_get_string(json_object_object_get(obj, name));
}

int iot_client_set_connection_string(const char *conn_str)
{
    int len = strlen(conn_str);
    strcpy(temp, conn_str);

    char *pch;
    pch = strtok(temp, SEMICOLON);

    while (pch != NULL)
    {
        String keyValuePair(pch);
        int equalPos = keyValuePair.indexOf(EQUAL_CHARACTOR);
        if (equalPos > 0 && equalPos < keyValuePair.length() - 1)
        {
            String key = keyValuePair.substring(0, equalPos);
            String value = keyValuePair.substring(equalPos + 1);
            key.trim();
            value.trim();
            if (strcmp(key.c_str(), HOSTNAME_TOKEN) == 0)
            {
                set_string(&hostNameString, value.c_str(), value.length());
            }
            else if (strcmp(key.c_str(), DEVICEID_TOKEN) == 0)
            {
                set_string(&deviceIdString, value.c_str(), value.length());
            }
            else if (strcmp(key.c_str(), DEVICEKEY_TOKEN) == 0)
            {
                set_string(&deviceKeyString, value.c_str(), value.length());
            }
            else
            {
                Serial.printf("Invalid connection string property: %s", key);
                return -1;
            }
        }
        else
        {
            Serial.printf("Invalid connection string: ", conn_str);
            return -1;
        }

        pch = strtok(NULL, SEMICOLON);
    }
    return 0;
}

int iot_client_blob_upload_step1(const char *blobName)
{
    if (blobName == NULL || validate_iot() != 0)
    {
        return -1;
    }

    sprintf(temp, BLOB_REQUEST_ENDPOINT, hostNameString, deviceIdString, blobName);
    Serial.printf("blob:%s\n",temp);
    HTTPClient blobRequest = HTTPClient(HTTP_GET, temp);
    blobRequest.set_header("Authorization", current_token);
    blobRequest.set_header("Accept", "application/json");
    const Http_Response *response = blobRequest.send();
    bool error = false;
    if (response == NULL)
    {
        Serial.println("iot_client_blob_upload_step1 failed with NULL response!");
        return -1;
    }
    if (response->status_code < 300)
    {
        json_object *jsonObject = json_tokener_parse(response->body);
        if (jsonObject != NULL)
        {
            const char *json_correlationId = get_json_string(jsonObject, "correlationId");
            const char *json_hostName = get_json_string(jsonObject, "hostName");
            const char *json_containerName = get_json_string(jsonObject, "containerName");
            const char *json_blobName = get_json_string(jsonObject, "blobName");
            const char *json_sasToken = get_json_string(jsonObject, "sasToken");

            if (json_correlationId == NULL || json_hostName == NULL || json_containerName == NULL || json_blobName == NULL || json_sasToken == NULL)
            {
                Serial.println("Error when parsing json object");
                error = true;
            }
            if (!error)
            {
                sprintf(temp2, "https://%s/%s/%s%s", json_hostName, json_containerName, json_blobName, json_sasToken);
                set_string(&sasUri, temp2, strlen(temp2));
                set_string(&correlationId, json_correlationId, strlen(json_correlationId));
            }
        }

        if (jsonObject != NULL)
            json_object_put(jsonObject);
    }

    return error ? -1 : 0;
}

int iot_client_blob_upload_step2(const char *content, int length)
{
    if (content == NULL || length <= 0 || length > MAX_UPLOAD_SIZE)
    {
        Serial.println("Content not valid to upload");
        return -1;
    }
    if (hostNameString == NULL || deviceIdString == NULL || deviceKeyString == NULL)
    {
        Serial.println("Iot hub connection string is not initialized");
        return -1;
    }
    if (sasUri == NULL)
    {
        Serial.println("Uri to get sas token is null");
        return -1;
    }
    Serial.printf("blob:%s\n",sasUri);
    HTTPClient uploadRequest = HTTPClient(HTTP_PUT, sasUri);
    uploadRequest.set_header("x-ms-blob-type", "BlockBlob");
    const Http_Response *response = uploadRequest.send(content, length);
    if (response == NULL)
    {
        Serial.println("iot_client_blob_upload_step2 failed!");
        return -1;
    }
    Serial.printf("Upload blob result: <%d> message %s\r\n", response->status_code, response->status_message);

    return !(response->status_code >= 200 && response->status_code < 300);
}

int complete_c2d_message(char *etag)
{
    if (etag == NULL || strlen(etag) < 2)
    {
        Serial.println("Invalid etag.");
        return -1;
    }
    if (validate_iot() != 0)
    {
        return -1;
    }
    etag[strlen(etag) - 1] = '\0';
    sprintf(temp, C2D_CB_ENDPOINT, hostNameString, deviceIdString, etag + 1);
    HTTPClient request = HTTPClient(HTTP_DELETE, temp);
    Serial.printf("URL:%s,token:%s\n",temp,current_token);
    request.set_header("Authorization", current_token);
    request.set_header("Accept", "application/json");
    const Http_Response *response = request.send();

    if (response == NULL)
    {
        Serial.println("Cannot delete message(Null Response).");
        return -1;
    }
    else
    {
        Serial.printf("Message deleted with status code %d\n",response->status_code);
    }

    return !(response->status_code >= 200 && response->status_code < 300);
}

const char *iot_client_get_c2d_message(char *etag)
{
    if (validate_iot() != 0)
    {
        return NULL;
    }
    sprintf(temp, C2D_ENDPOINT, hostNameString, deviceIdString);
    HTTPClient request = HTTPClient(HTTP_GET, temp);
    request.set_header("Authorization", current_token);
    Serial.printf("URL:%s,token:%s\n",temp,current_token);
    request.set_header("Accept", "application/json");
    const Http_Response *response = request.send();

    if (response == NULL)
    {
        Serial.println("Cannot get message(Null Response).");
        return NULL;
    }

    KEYVALUE *header = (KEYVALUE *)response->headers;
    while (header->prev != NULL)
    {
        if (strcmp("ETag", header->prev->key) == 0)
        {
            set_string(&etag, header->value, strlen(header->value));
            Serial.printf("ETag: %s\n", etag);
        }

        header = header->prev;
    }
    return response->body != NULL ? strdup(response->body) : NULL;
}


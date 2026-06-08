/*
 * WFInfo DBMON bridge - captures OutputDebugString via DBWIN_BUFFER.
 *
 * Pure Win32, no .NET runtime. Compiles to ~15 KB with MinGW vs 13 MB
 * self-contained .NET, uses <1 MB RAM vs ~86 MB.
 *
 * Build:
 *   x86_64-w64-mingw32-gcc -O2 -s -o WFInfo.DbMon.exe dbmon.c
 *
 * Protocol (matches the .NET original exactly):
 *   stderr: "DBMON_READY\n"  when listening begins
 *   stderr: "DBMON_ERROR: ...\n" on fatal error
 *   stdout: one line per OutputDebugString message (flushed immediately)
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* DBWIN_BUFFER layout: DWORD pid @ offset 0, char data[4092] @ offset 4 */
#define BUFFER_SIZE 4096
#define DATA_OFFSET 4
#define DATA_SIZE   (BUFFER_SIZE - DATA_OFFSET)

static HANDLE hStdout;
static HANDLE hStderr;

static void WriteStr(HANDLE h, const char *s)
{
    DWORD len = 0;
    while (s[len]) len++;
    DWORD written;
    WriteFile(h, s, len, &written, NULL);
}

static void Die(const char *msg)
{
    WriteStr(hStderr, "DBMON_ERROR: ");
    WriteStr(hStderr, msg);
    WriteStr(hStderr, "\n");
    FlushFileBuffers(hStderr);
    ExitProcess(1);
}

int main(void)
{
    hStdout = GetStdHandle(STD_OUTPUT_HANDLE);
    hStderr = GetStdHandle(STD_ERROR_HANDLE);

    /* INVALID_HANDLE_VALUE = backed by system page file, not a disk file */
    HANDLE hMapping = CreateFileMappingA(
        INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, BUFFER_SIZE, "DBWIN_BUFFER");
    if (!hMapping)
        Die("CreateFileMapping failed");

    char *pBuffer = (char *)MapViewOfFile(hMapping, FILE_MAP_READ, 0, 0, BUFFER_SIZE);
    if (!pBuffer)
        Die("MapViewOfFile failed");

    /* Auto-reset events for DBWIN producer/consumer handshake */
    HANDLE hBufferReady = CreateEventA(NULL, FALSE, FALSE, "DBWIN_BUFFER_READY");
    if (!hBufferReady)
        Die("CreateEvent DBWIN_BUFFER_READY failed");

    HANDLE hDataReady = CreateEventA(NULL, FALSE, FALSE, "DBWIN_DATA_READY");
    if (!hDataReady)
        Die("CreateEvent DBWIN_DATA_READY failed");

    WriteStr(hStderr, "DBMON_READY\n");
    FlushFileBuffers(hStderr);

    SetEvent(hBufferReady);

    for (;;)
    {
        DWORD wait = WaitForSingleObject(hDataReady, 2000);

        if (wait == WAIT_TIMEOUT)
            continue;
        if (wait != WAIT_OBJECT_0)
            break;

        const char *data = pBuffer + DATA_OFFSET;

        int len = 0;
        while (len < DATA_SIZE && data[len] != '\0')
            len++;

        while (len > 0 && (data[len - 1] == ' '  || data[len - 1] == '\t' ||
                           data[len - 1] == '\r' || data[len - 1] == '\n'))
            len--;

        int start = 0;
        while (start < len && (data[start] == ' '  || data[start] == '\t' ||
                               data[start] == '\r' || data[start] == '\n'))
            start++;

        if (len > start)
        {
            DWORD written;
            if (!WriteFile(hStdout, data + start, len - start, &written, NULL))
                break; /* stdout closed, parent is gone */
            WriteFile(hStdout, "\n", 1, &written, NULL);
            FlushFileBuffers(hStdout);
        }

        SetEvent(hBufferReady);
    }

    UnmapViewOfFile(pBuffer);
    CloseHandle(hMapping);
    CloseHandle(hBufferReady);
    CloseHandle(hDataReady);
    return 0;
}
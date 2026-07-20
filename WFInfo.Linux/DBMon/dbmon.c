/*
 * WFInfo DBMON bridge, captures OutputDebugString via DBWIN_BUFFER.
 * Also handles cursor position queries and continuous cursor file output
 * for Wayland reward selection.
 *
 * Pure Win32, no .NET runtime. Compiles to ~15 KB with MinGW vs 13 MB
 * self-contained .NET, uses <1 MB RAM vs ~86 MB.
 *
 * Build:
 *   zig cc -target x86_64-windows-gnu -O2 -o WFInfo.DbMon.exe dbmon.c
 *
 * Protocol:
 *   stderr: "DBMON_READY\n"  when listening begins
 *   stderr: "DBMON_ERROR: ...\n" on fatal error
 *   stdout: one line per OutputDebugString message (flushed immediately)
 *   stdin:  "CURSOR\n" -> stdout: "CURSOR x y\n" (GetCursorPos response)
 *   stdin:  "FOCUS\n"  -> stdout: "FOCUS 1\n" or "FOCUS 0\n" (is Warframe focused?)
 *
 * Cursor file (for native overlay to read directly):
 *   Writes to Z:\tmp\wfinfo_cursor (/tmp/wfinfo_cursor on Linux).
 *   Format: "x y buttons seq\n"
 *     buttons: bitmask (1=LMB, 2=RMB, 4=MMB)
 *   Only writes on change, polls at ~120 Hz.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>

/* DBWIN_BUFFER layout: DWORD pid @ offset 0, char data[4092] @ offset 4 */
#define BUFFER_SIZE 4096
#define DATA_OFFSET 4
#define DATA_SIZE   (BUFFER_SIZE - DATA_OFFSET)

static HANDLE hStdin;
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

/* Event signaled by stdin reader thread when a CURSOR or FOCUS query arrives */
static HANDLE hCursorRequest;
static HANDLE hFocusRequest;

static void HandleFocusQuery(void)
{
    /* Find Warframe.x64.exe among running processes, compare its PID
     * to the PID that owns the foreground window. */
    HWND fg = GetForegroundWindow();
    if (fg == NULL)
    {
        DWORD written;
        WriteFile(hStdout, "FOCUS 1\n", 8, &written, NULL);
        FlushFileBuffers(hStdout);
        return;
    }

    DWORD fgPid = 0;
    GetWindowThreadProcessId(fg, &fgPid);

    /* Walk all top-level windows to find Warframe's PID.
     * We check if the foreground window's PID matches the PID of any
     * window whose title contains "Warframe". */
    DWORD wfPid = 0;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap != INVALID_HANDLE_VALUE)
    {
        PROCESSENTRY32 pe;
        pe.dwSize = sizeof(pe);
        if (Process32First(snap, &pe))
        {
            do
            {
                /* Compare executable name case-insensitively */
                const char *exe = pe.szExeFile;
                if ((exe[0] == 'W' || exe[0] == 'w') &&
                    (exe[1] == 'a' || exe[1] == 'A') &&
                    (exe[2] == 'r' || exe[2] == 'R'))
                {
                    /* Check full name: Warframe.x64.exe */
                    int match = 1;
                    const char *expect = "Warframe.x64.exe";
                    for (int i = 0; expect[i]; i++)
                    {
                        char c = exe[i];
                        char e = expect[i];
                        if (c >= 'A' && c <= 'Z') c += 32;
                        if (e >= 'A' && e <= 'Z') e += 32;
                        if (c != e) { match = 0; break; }
                    }
                    if (match)
                    {
                        wfPid = pe.th32ProcessID;
                        break;
                    }
                }
            } while (Process32Next(snap, &pe));
        }
        CloseHandle(snap);
    }

    int focused = (wfPid != 0 && fgPid == wfPid) ? 1 : 0;
    char resp[16];
    resp[0] = 'F'; resp[1] = 'O'; resp[2] = 'C';
    resp[3] = 'U'; resp[4] = 'S'; resp[5] = ' ';
    resp[6] = '0' + (char)focused;
    resp[7] = '\n';
    DWORD written;
    WriteFile(hStdout, resp, 8, &written, NULL);
    FlushFileBuffers(hStdout);
}

static void HandleCursorQuery(void)
{
    POINT p;
    if (GetCursorPos(&p))
    {
        char resp[64];
        int rlen = 0;
        resp[rlen++] = 'C'; resp[rlen++] = 'U'; resp[rlen++] = 'R';
        resp[rlen++] = 'S'; resp[rlen++] = 'O'; resp[rlen++] = 'R';
        resp[rlen++] = ' ';

        char tmp[16];
        int ti;

        LONG vx = p.x;
        int neg = 0;
        if (vx < 0) { neg = 1; vx = -vx; }
        ti = 0;
        do { tmp[ti++] = '0' + (char)(vx % 10); vx /= 10; } while (vx > 0);
        if (neg) resp[rlen++] = '-';
        while (ti > 0) resp[rlen++] = tmp[--ti];

        resp[rlen++] = ' ';

        LONG vy = p.y;
        neg = 0;
        if (vy < 0) { neg = 1; vy = -vy; }
        ti = 0;
        do { tmp[ti++] = '0' + (char)(vy % 10); vy /= 10; } while (vy > 0);
        if (neg) resp[rlen++] = '-';
        while (ti > 0) resp[rlen++] = tmp[--ti];

        resp[rlen++] = '\n';

        DWORD written;
        WriteFile(hStdout, resp, rlen, &written, NULL);
        FlushFileBuffers(hStdout);
    }
}

/* Cursor file writer thread: continuously writes cursor pos + button state
 * to /tmp/wfinfo_cursor for the native overlay to read. */
static DWORD WINAPI CursorFileThread(LPVOID param)
{
    (void)param;
    const char *path = "Z:\\tmp\\wfinfo_cursor";
    LONG prev_x = -99999, prev_y = -99999;
    int prev_btn = -1;
    unsigned int seq = 0;

    for (;;)
    {
        POINT p;
        if (GetCursorPos(&p))
        {
            int btn = 0;
            if (GetAsyncKeyState(VK_LBUTTON) & 0x8000) btn |= 1;
            if (GetAsyncKeyState(VK_RBUTTON) & 0x8000) btn |= 2;
            if (GetAsyncKeyState(VK_MBUTTON) & 0x8000) btn |= 4;

            if (p.x != prev_x || p.y != prev_y || btn != prev_btn)
            {
                HANDLE hFile = CreateFileA(path, GENERIC_WRITE, 0, NULL,
                    CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
                if (hFile != INVALID_HANDLE_VALUE)
                {
                    char buf[64];
                    int len = 0;
                    /* Format: "x y btn seq\n" */
                    char tmp[16];
                    int ti;

                    /* x */
                    LONG v = p.x;
                    int neg = 0;
                    if (v < 0) { neg = 1; v = -v; }
                    ti = 0;
                    do { tmp[ti++] = '0' + (char)(v % 10); v /= 10; } while (v > 0);
                    if (neg) buf[len++] = '-';
                    while (ti > 0) buf[len++] = tmp[--ti];
                    buf[len++] = ' ';

                    /* y */
                    v = p.y;
                    neg = 0;
                    if (v < 0) { neg = 1; v = -v; }
                    ti = 0;
                    do { tmp[ti++] = '0' + (char)(v % 10); v /= 10; } while (v > 0);
                    if (neg) buf[len++] = '-';
                    while (ti > 0) buf[len++] = tmp[--ti];
                    buf[len++] = ' ';

                    /* btn */
                    buf[len++] = '0' + (char)btn;
                    buf[len++] = ' ';

                    /* seq */
                    ++seq;
                    unsigned int sv = seq;
                    ti = 0;
                    do { tmp[ti++] = '0' + (char)(sv % 10); sv /= 10; } while (sv > 0);
                    while (ti > 0) buf[len++] = tmp[--ti];
                    buf[len++] = '\n';

                    DWORD written;
                    WriteFile(hFile, buf, len, &written, NULL);
                    CloseHandle(hFile);
                }
                prev_x = p.x;
                prev_y = p.y;
                prev_btn = btn;
            }
        }
        Sleep(8);  /* ~120 Hz */
    }
    return 0;
}

/* Stdin reader thread: blocking ReadFile, signals event on CURSOR command */
static DWORD WINAPI StdinReaderThread(LPVOID param)
{
    (void)param;
    char buf[64];
    DWORD nRead;

    while (ReadFile(hStdin, buf, sizeof(buf) - 1, &nRead, NULL) && nRead > 0)
    {
        buf[nRead] = '\0';
        /* Strip trailing whitespace */
        while (nRead > 0 && (buf[nRead - 1] == '\n' || buf[nRead - 1] == '\r'))
            buf[--nRead] = '\0';

        if (nRead >= 6 && buf[0] == 'C' && buf[1] == 'U' && buf[2] == 'R'
            && buf[3] == 'S' && buf[4] == 'O' && buf[5] == 'R')
        {
            SetEvent(hCursorRequest);
        }
        else if (nRead >= 5 && buf[0] == 'F' && buf[1] == 'O' && buf[2] == 'C'
            && buf[3] == 'U' && buf[4] == 'S')
        {
            SetEvent(hFocusRequest);
        }
    }
    return 0;
}

int main(void)
{
    hStdin  = GetStdHandle(STD_INPUT_HANDLE);
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

    /* Stdin reader thread for cursor and focus queries */
    hCursorRequest = CreateEventA(NULL, FALSE, FALSE, NULL);
    hFocusRequest = CreateEventA(NULL, FALSE, FALSE, NULL);
    CreateThread(NULL, 0, StdinReaderThread, NULL, 0, NULL);

    /* Cursor file writer thread for native overlay */
    CreateThread(NULL, 0, CursorFileThread, NULL, 0, NULL);

    WriteStr(hStderr, "DBMON_READY\n");
    FlushFileBuffers(hStderr);

    SetEvent(hBufferReady);

    HANDLE waitHandles[3] = { hDataReady, hCursorRequest, hFocusRequest };

    for (;;)
    {
        DWORD wait = WaitForMultipleObjects(3, waitHandles, FALSE, 2000);

        if (wait == WAIT_OBJECT_0)
        {
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
        else if (wait == WAIT_OBJECT_0 + 1)
        {
            HandleCursorQuery();
        }
        else if (wait == WAIT_OBJECT_0 + 2)
        {
            HandleFocusQuery();
        }
    }

    UnmapViewOfFile(pBuffer);
    CloseHandle(hMapping);
    CloseHandle(hBufferReady);
    CloseHandle(hDataReady);
    return 0;
}
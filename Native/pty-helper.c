#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <pty.h>
#include <sys/ioctl.h>
#include <sys/wait.h>
#include <errno.h>

// Spawns a process in a new PTY.
// Returns child PID on success, -1 on failure.
// master_fd is set to the PTY master file descriptor.
int spawn_in_pty(const char *file, char *const argv[],
                 int cols, int rows, int *master_fd)
{
    struct winsize ws = {
        .ws_row = (unsigned short)rows,
        .ws_col = (unsigned short)cols,
        .ws_xpixel = 0,
        .ws_ypixel = 0,
    };

    int master = -1;
    pid_t pid = forkpty(&master, NULL, NULL, &ws);

    if (pid < 0) {
        return -1;
    }

    if (pid == 0) {
        // Child process — exec immediately, no managed code runs here
        setenv("TERM", "xterm-256color", 1);
        setenv("COLORTERM", "truecolor", 1);
        execvp(file, argv);
        _exit(127); // exec failed
    }

    // Parent
    *master_fd = master;
    return (int)pid;
}

// Resize the PTY
int resize_pty(int master_fd, int cols, int rows)
{
    struct winsize ws = {
        .ws_row = (unsigned short)rows,
        .ws_col = (unsigned short)cols,
    };
    return ioctl(master_fd, TIOCSWINSZ, &ws);
}

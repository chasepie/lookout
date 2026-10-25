/** Collecting what a Script's process writes to stdout and stderr, as the response's console output */
import type { ChildProcess } from 'node:child_process';

/** How many characters of a Script's console output the runner keeps, so a Script that logs without end can't exhaust it */
const maxConsoleOutputLength = 256 * 1024;

const cutOutputMarker = '[Earlier output was cut]\n';

/**
 * How much of a Script's stderr to keep apart, for V8's out-of-memory message, which V8 follows with a native
 * stack trace
 */
const stderrTailLength = 16 * 1024;

/** Keeps the end of the text appended to it once there's more than `maxLength` characters, marking the cut */
function textTail(maxLength: number) {
  let text = '';
  let cut = false;
  return {
    append: (chunk: string) => {
      text += chunk;
      // Trimming only once there's twice too much, so heavy output isn't copied on every chunk
      if (text.length > 2 * maxLength) {
        text = text.slice(-maxLength);
        cut = true;
      }
    },
    text: () => {
      if (!cut && text.length <= maxLength) {
        return text;
      }
      const kept = text.slice(-maxLength);
      // Without the second half of a character the cut went through, which on its own isn't valid text
      return cutOutputMarker + (/^[\uDC00-\uDFFF]/.test(kept) ? kept.slice(1) : kept);
    },
  };
}

/**
 * Collects a process's stdout and stderr as they arrive, keeping only the end once there's too much. The end is
 * what explains a failure. The end of stderr is kept apart as well, since only there can V8 say the process ran
 * out of memory.
 */
export function collectConsoleOutput(child: ChildProcess) {
  const output = textTail(maxConsoleOutputLength);
  const stderr = textTail(stderrTailLength);
  // Decoding as a stream, so a character split across chunks survives
  child.stdout?.setEncoding('utf8').on('data', output.append);
  child.stderr?.setEncoding('utf8').on('data', (chunk: string) => {
    output.append(chunk);
    stderr.append(chunk);
  });
  return { consoleOutput: output.text, stderr: stderr.text };
}

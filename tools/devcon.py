"""Developer console client: python devcon.py "class HB.Golf - ConnectionManager" ["second command" ...]
Talks to the running game over the local pipe opened when "DevConsole": true is set in config.json."""
import sys

PIPE = '\\\\.\\pipe\\openfairway-dev'


def main():
    with open(PIPE, 'r+b', buffering=0) as pipe:
        for command in sys.argv[1:]:
            pipe.write((command + '\n').encode('utf-8'))
            buffer = b''
            while not buffer.replace(b'\r', b'').endswith(b'\n.\n'):
                chunk = pipe.read(65536)
                if not chunk:
                    break
                buffer += chunk
            text = buffer.decode('utf-8', 'replace').replace('\r', '')
            if len(sys.argv) > 2:
                print('> ' + command)
            print(text[:-3] if text.endswith('\n.\n') else text)


if __name__ == '__main__':
    main()

import multiprocessing

# Required for the PyInstaller exe on Windows. Pool workers re-launch mkpfs.exe;
# without this they fall through to argparse as "parent_pid=..." and compression fails.
multiprocessing.freeze_support()

from mkpfs.cli import main  # noqa: E402

if __name__ == "__main__":
    raise SystemExit(main())

import os

targets = [
    r"C:\Users\sunme\AppData\Local\Microsoft\VisualStudio\17.0_8e8044dc\Extensions\bboplj5t.kqv\ZooVs.Package.dll",
    r"E:\code\cline-vsex\zoo-vsix\src\ZooVs.Package\bin\Release\net472\ZooVs.Package.dll",
]
for name in targets:
    b = open(name, "rb").read()
    u16 = "vs_find_calls".encode("utf-16-le")
    parts = name.split(os.sep)
    print(parts[-4], parts[-1],
          "| vs_find_calls(UTF16):", b.count(u16),
          "| taskkill(UTF16):", b.count("taskkill".encode("utf-16-le")))

exe = open(r"C:\Users\sunme\AppData\Local\Microsoft\VisualStudio\17.0_8e8044dc\Extensions\bboplj5t.kqv\assets\roslyn\ZooMcpServer.exe", "rb").read()
print("exe find_calls:", exe.count("find_calls".encode("utf-16-le")) + exe.count(b"find_calls"))


import os

path = r"c:\Users\bbush\source\repos\UnityExternalModdingTool\Database\database.txt"
if os.path.exists(path):
    with open(path, 'rb') as f:
        content = f.read()
    
    # Normalize to \n first, remove \r
    text = content.decode('utf-8', errors='ignore')
    text = text.replace('\r\n', '\n').replace('\r', '')
    
    # Write back with \r\n
    lines = text.split('\n')
    with open(path, 'w', encoding='utf-8', newline='\r\n') as f:
        for line in lines:
            f.write(line + '\n')
    print("Done")
else:
    print("File not found")

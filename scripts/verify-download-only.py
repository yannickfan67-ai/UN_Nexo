"""Check the actual desktop dependency graph rather than a UI-only feature flag."""
import json
from pathlib import Path
import sys
path = Path(sys.argv[1] if len(sys.argv) > 1 else 'src/UN.Nexo.Desktop/bin/Release/net10.0/UN_Nexo.deps.json')
deps = json.loads(path.read_text())
libraries = list(deps['libraries'])
assert any(name.startswith('UN.Nexo.Downloads/') for name in libraries), 'Shipping downloader assembly is missing'
assert not any('Microsoft.Identity' in name or name.startswith('UN.Nexo.Core/') for name in libraries), 'Authentication/legacy core was packaged'
print('PASS: shipping desktop uses Downloads and excludes legacy authentication/game core')

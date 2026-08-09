import { cpSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
cpSync(join(root, 'src/tokens'), join(root, 'dist/tokens'), { recursive: true });

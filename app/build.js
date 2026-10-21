// Compiles the native app with the C# compiler that ships with Windows (.NET Framework 4.8, C# 5): no SDK, no NuGet.
import { readdirSync } from 'node:fs';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const run = promisify(execFile);
export const CSC = 'C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe';
const REFS = ['System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll'];
const src = f => path.join(here, f);

async function csc(args) {
  try {
    await run(CSC, ['/nologo', '/codepage:65001', '/utf8output', ...REFS.map(r => '/reference:' + r), ...args], { windowsHide: true, maxBuffer: 16 << 20 });
  } catch (e) {
    throw new Error(`C# compile failed:\n${e.stdout || ''}${e.stderr || e.message}`);
  }
}

const literal = s => '"' + String(s).replaceAll('\\', '\\\\').replaceAll('"', '\\"') + '"';

// out: the exe's path. name becomes Application.ProductName, which the app uses as its name.
export async function compileApp({ out, name, version, icon }) {
  const [a, b, c] = `${version}.0.0`.split('.').map(n => Number(n) & 0xffff); // a.b.c.0, as install.js versionInfo()
  const tmp = await fs.mkdtemp(path.join(os.tmpdir(), 'arrow-build-'));
  try {
    const info = path.join(tmp, 'AssemblyInfo.cs');
    await fs.writeFile(info, `using System.Reflection;
[assembly: AssemblyTitle(${literal(name)})]
[assembly: AssemblyProduct(${literal(name)})]
[assembly: AssemblyDescription(${literal(name)})]
[assembly: AssemblyVersion("${a}.${b}.${c}.0")]
[assembly: AssemblyFileVersion("${a}.${b}.${c}.0")]
[assembly: AssemblyInformationalVersion(${literal(version)})]
`);
    const sources = readdirSync(here).filter(f => f.endsWith('.cs') && f !== 'Tests.cs').map(src);
    // x64: a PE32+ exe, like the node.exe-based one install.js reads (resources()); AnyCPU would be PE32
    await csc(['/target:winexe', '/platform:x64', '/main:Arrow.Program', '/optimize+', `/win32icon:${icon}`, `/win32manifest:${src('app.manifest')}`, `/out:${out}`, info, ...sources]);
  } finally {
    await fs.rm(tmp, { recursive: true, force: true });
  }
}

// A console exe that runs Tests.cs against the pure logic and the API client
export async function compileTests(out) {
  await csc(['/target:exe', '/main:Arrow.Tests', `/out:${out}`, src('Core.cs'), src('Api.cs'), src('Tests.cs')]);
}

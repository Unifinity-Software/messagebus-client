// Image build only. No runtime download or unverified executable is permitted.
import fs from 'node:fs';
import crypto from 'node:crypto';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { spawnSync } from 'node:child_process';
if ((process.argv[2] || 'amd64') !== 'amd64') process.exit(0);
const version = 'release-20261116.0';
const sha512 = 'c87d792d5842efb70c38ee822124cfe0821a547715e7abcf3f2b14997cd02784edaa0b91e238ec5c27a25a1b9e00bf98113398eb62a0778d8d20d4e222f8b0e0';
const temporary = fs.mkdtempSync('/tmp/mbus-gvisor-build-');
try {
    const archive = temporary + '/gvisor.tar.bz2';
    const response = await fetch(`https://storage.googleapis.com/gvisor/releases/release/20261116.0/x86_64/gvisor.tar.bz2`, { signal: AbortSignal.timeout(60000) });
    if (!response.ok) throw Error('Official gVisor download failed.');
    await pipeline(Readable.fromWeb(response.body), fs.createWriteStream(archive));
    const hash = crypto.createHash('sha512');
    for await (const chunk of fs.createReadStream(archive)) hash.update(chunk);
    if (hash.digest('hex') !== sha512) throw Error('Official gVisor checksum mismatch.');
    const output = '/usr/local/lib/mbus/gvisor';
    fs.mkdirSync(output, { recursive: true });
    const result = spawnSync('/usr/bin/perl', ['-MIO::Uncompress::Bunzip2=bunzip2,$Bunzip2Error', '-e',
        'my ($source,$dest)=@ARGV; open my $tar,"|-","tar","-xf","-","-C",$dest or die; bunzip2($source=>$tar) or die $Bunzip2Error; close $tar or die;', archive, output],
        { stdio: 'inherit', timeout: 60000 });
    if (result.status !== 0) throw Error('Verified gVisor extraction failed.');
    fs.writeFileSync(output + '/version.json', JSON.stringify({ version, architecture: 'amd64', sha512 }));
} finally { fs.rmSync(temporary, { recursive: true, force: true }); }

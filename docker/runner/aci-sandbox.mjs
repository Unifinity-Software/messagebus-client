// Trusted image helper. Its state directory is never mounted into a workload.
// runsc and gvisor-bin are the matching, checksum-pinned image dependency.
import fs from 'node:fs';
import path from 'node:path';
import net from 'node:net';
import { spawn, spawnSync } from 'node:child_process';

const runtime = '/usr/local/lib/mbus/gvisor/runsc';
const safeEnv = { PATH: '/usr/local/bin:/usr/bin:/bin', LC_ALL: 'C' };
const [mode, directory] = process.argv.slice(2);
const root = fs.realpathSync(directory);
if (process.platform !== 'linux' || process.arch !== 'x64' || process.getuid() !== 10001
    || root !== directory || !/^launch-[a-f0-9]{32}$/.test(path.basename(root))) throw Error('Invalid protected runtime directory.');
const stat = fs.statSync(root);
if (stat.uid !== 10001 || (stat.mode & 0o077) !== 0) throw Error('Invalid protected runtime ownership.');
const id = path.basename(root);
const journalFile = root + '/journal.json';
const stateFile = root + '/state/' + id + '_sandbox:' + id + '.state';
let journal = { id, root, peers: {}, complete: false, uncertain: false, launcher: null, safe: false };
function save() {
    fs.writeFileSync(journalFile + '.next', JSON.stringify(journal), { mode: 0o600 });
    fs.renameSync(journalFile + '.next', journalFile);
}
function identity(pid) {
    try {
        const raw = fs.readFileSync('/proc/' + pid + '/stat', 'utf8');
        const fields = raw.slice(raw.lastIndexOf(')') + 2).trim().split(/\s+/);
        return { start: fields[19], state: fields[0], ppid: Number(fields[1]) };
    } catch { return null; }
}
function kernelIdentity(pid) {
    // Match official runsc ProcessIdentity: PIDFS pidfd inode, or zero before PIDFS.
    const code = 'my $fd=syscall(434,0+$ARGV[0],0); $fd>=0 or die; open my $f,"<&=",$fd or die; my $b="\\0"x120; syscall(138,$fd,$b)==0 or die; my @s=stat($f); @s or die; print unpack("q",$b)==0x50494446 ? $s[1] : 0;';
    const result = spawnSync('/usr/bin/perl', ['-e', code, String(pid)], { env: safeEnv, encoding: 'utf8', timeout: 1000 });
    if (result.status !== 0 || !/^\d+$/.test(result.stdout)) throw Error('Cannot verify protected process identity.');
    return result.stdout;
}
function descendedFromLauncher(pid) {
    let current = pid;
    for (let depth = 0; depth < 24; depth++) {
        const now = identity(current);
        if (!now) return false;
        if (current === journal.launcher?.pid) return now.start === journal.launcher.start && now.state !== 'Z';
        if (!journal.launcher || BigInt(now.start) < BigInt(journal.launcher.start) || now.ppid <= 0 || now.ppid === current) return false;
        current = now.ppid;
    }
    return false;
}
function inspect() {
    try {
        if (fs.statSync(stateFile).size > 1048576) throw Error('Invalid runtime record size.');
        const record = JSON.parse(fs.readFileSync(stateFile, 'utf8'));
        if (record.id !== id || (record.sandbox && record.sandbox.id !== id) || record.sandbox?.netgoferPid > 0) throw Error('Unexpected runtime record.');
        for (const [kind, pid, native] of [['gofer', record.goferPid, record.goferPidIdentity ?? 0], ['sentry', record.sandbox?.pid, record.sandbox?.pidIdentity ?? 0]]) {
            if (!pid) continue;
            if (!Number.isSafeInteger(pid) || pid <= 0 || !Number.isSafeInteger(native) || native < 0) throw Error('Invalid peer identity.');
            const previous = journal.peers[kind], now = identity(pid);
            if (previous) {
                if (previous.pid !== pid || previous.native !== native || (now && previous.start !== now.start)) throw Error('Runtime peer identity changed.');
                continue;
            }
            if (!now || !descendedFromLauncher(pid) || kernelIdentity(pid) !== String(native) || identity(pid)?.start !== now.start) throw Error('Runtime peer birth is unproven.');
            journal.peers[kind] = { pid, start: now.start, native };
        }
        journal.complete = Boolean(journal.peers.gofer && journal.peers.sentry);
        save();
    } catch (error) {
        if (error.code !== 'ENOENT') { journal.uncertain = true; save(); }
    }
}
function command(args) {
    return spawnSync(runtime, ['--root=' + root + '/state', ...args], { env: safeEnv, encoding: 'utf8', timeout: 2500, maxBuffer: 1048576 });
}
async function cleanup() {
    inspect();
    if (!journal.complete || journal.uncertain) { journal.safe = false; save(); return false; }
    for (const peer of Object.values(journal.peers)) {
        const now = identity(peer.pid);
        if (now && now.start !== peer.start) { journal.uncertain = true; save(); return false; }
    }
    command(['kill', '--all', id, 'SIGKILL']);
    inspect();
    if (journal.uncertain) return false;
    const deleted = command(['delete', '--force', id]);
    const until = Date.now() + 2000;
    const active = () => Object.values(journal.peers).some(peer => { const now = identity(peer.pid); return now && now.start === peer.start && now.state !== 'Z'; });
    while (active() && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 25));
    const listed = command(['list', '--format=json']);
    let empty = false;
    try { const all = JSON.parse(listed.stdout); empty = listed.status === 0 && (all === null || (Array.isArray(all) && all.length === 0)); } catch { }
    journal.safe = deleted.status === 0 && empty && !active();
    save();
    if (journal.safe) finishControlCleanup();
    return journal.safe;
}
function finishControlCleanup() {
    if (!journal.control) return;
    if (!/^\/tmp\/mbus-gate-[A-Za-z0-9]+$/.test(journal.control)) throw Error('Invalid owned gate directory.');
    if (fs.existsSync(journal.control)) {
        if (journal.controlCreated !== true) throw Error('Gate ownership was not fully recorded.');
        const control = fs.realpathSync(journal.control);
        if (control !== journal.control) throw Error('Invalid owned gate directory.');
        fs.rmSync(control, { recursive: true });
    }
    if (fs.existsSync(journal.control)) throw Error('Owned gate directory remains.');
    delete journal.control;
    delete journal.controlCreated;
    save();
}
const bind = (source, destination, readonly = true) => ({ source, destination, type: 'bind', options: ['rbind', readonly ? 'ro' : 'rw', 'nosuid', 'nodev'] });
const relay = `
const net=require('node:net'),{spawn}=require('node:child_process');
const [gate,proxy,port,file,...args]=process.argv.slice(1);
const ownership=net.connect(gate);ownership.once('error',()=>process.exit(1));
let answer='';ownership.on('data',b=>answer+=b);ownership.once('end',()=>{
 if(answer!=='owned')process.exit(1);
 const listener=net.createServer({allowHalfOpen:true},socket=>{
  const peer=net.createConnection({path:proxy,allowHalfOpen:true});
  socket.on('error',()=>peer.destroy());peer.on('error',()=>socket.destroy());socket.pipe(peer).pipe(socket);
 });
 listener.once('error',()=>process.exit(1));listener.listen(Number(port),'127.0.0.1',()=>{
  const child=spawn(file,args,{stdio:'inherit'});child.once('error',()=>process.exit(1));child.once('exit',code=>process.exit(code??1));
  for(const signal of ['SIGTERM','SIGINT'])process.on(signal,()=>child.kill(signal));
 });
});`;

if (mode === 'cleanup') {
    // A single atomic transition fences a launch that was prepared but never
    // started. A concurrent helper can win this transition, never both sides.
    try { fs.renameSync(root + '/prepared', root + '/cancelled'); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (fs.existsSync(root + '/cancelled') && !fs.existsSync(root + '/starting') && !fs.existsSync(root + '/state')) process.exit(0);
    journal = JSON.parse(fs.readFileSync(journalFile, 'utf8'));
    if (journal.id !== id || journal.root !== root || typeof journal.peers !== 'object') throw Error('Invalid owned runtime journal.');
    const safe = journal.safe || await cleanup();
    if (safe) finishControlCleanup();
    process.exitCode = safe ? 0 : 1;
} else if (mode === 'run') {
    fs.renameSync(root + '/prepared', root + '/starting');
    const plan = JSON.parse(fs.readFileSync(root + '/plan.json', 'utf8'));
    const canonical = value => { if (!path.isAbsolute(value) || fs.realpathSync(value) !== value) throw Error('Noncanonical protected mount source.'); return value; };
    const run = canonical(plan.run), work = canonical(plan.work), sockets = canonical(plan.sockets);
    if (work !== run + '/work' || root === run || root.startsWith(run + '/') || sockets === run || sockets.startsWith(run + '/') || run.startsWith(sockets + '/')) throw Error('Overlapping protected mount sources.');
    if (!plan.cwd.startsWith(run + '/') && plan.cwd !== run) throw Error('Invalid protected working directory.');
    fs.mkdirSync(root + '/bundle/rootfs', { recursive: true, mode: 0o700 });
    fs.mkdirSync(root + '/state', { mode: 0o700 });
    // Record the exact prospective path before creating it. A crash cannot
    // leave an undiscoverable randomly named gate directory behind.
    journal.control = '/tmp/mbus-gate-' + id.slice(7);
    save();
    fs.mkdirSync(journal.control, { mode: 0o700 });
    journal.controlCreated = true;
    save();
    const control = '/run/mbus-control';
    const spec = {
        ociVersion: '1.0.2', root: { path: 'rootfs', readonly: true }, hostname: 'mbus-task',
        process: { terminal: false, user: { uid: 10001, gid: 10001 }, cwd: plan.cwd,
            args: ['/usr/local/bin/node', '-e', relay, '--', control + '/gate.sock', sockets + '/proxy.sock', '47475', plan.file, ...plan.args],
            env: Object.entries(plan.env).filter(([, value]) => value !== null).map(([key, value]) => key + '=' + value),
            capabilities: { bounding: [], effective: [], inheritable: [], permitted: [], ambient: [] }, noNewPrivileges: true },
        mounts: [
            { destination: '/', type: 'tmpfs', source: 'tmpfs', options: ['mode=0755', 'nosuid', 'nodev'] },
            { destination: '/proc', type: 'proc', source: 'proc', options: ['nosuid', 'nodev', 'noexec'] },
            { destination: '/dev', type: 'tmpfs', source: 'tmpfs', options: ['mode=0755', 'nosuid'] },
            { destination: '/dev/pts', type: 'devpts', source: 'devpts', options: ['nosuid', 'noexec', 'newinstance', 'ptmxmode=0666', 'mode=0620'] },
            ...['/tmp', '/var/tmp', '/dev/shm'].map(destination => ({ destination, type: 'tmpfs', source: 'tmpfs', options: ['mode=1777', 'nosuid', 'nodev'] })),
            bind('/usr', '/usr'), bind('/usr/bin', '/bin'), bind('/usr/sbin', '/sbin'), bind('/usr/lib', '/lib'), bind('/usr/lib64', '/lib64'),
            bind('/etc/ssl/certs', '/etc/ssl/certs'), bind('/etc/passwd', '/etc/passwd'), bind('/etc/group', '/etc/group'),
            bind(run, run, false), bind(work, work, Boolean(plan.readOnly)), bind(sockets, sockets), bind(journal.control, control),
        ],
        linux: { namespaces: [{ type: 'user' }], uidMappings: [{ containerID: 10001, hostID: 10001, size: 1 }], gidMappings: [{ containerID: 10001, hostID: 10001, size: 1 }] },
    };
    fs.writeFileSync(root + '/bundle/config.json', JSON.stringify(spec), { mode: 0o600 });
    const gate = net.createServer(socket => {
        inspect();
        if (!journal.complete || journal.uncertain || Object.values(journal.peers).some(peer => { const now = identity(peer.pid); return !now || now.start !== peer.start || now.state === 'Z'; })) socket.destroy();
        else socket.end('owned');
    });
    await new Promise((resolve, reject) => { gate.once('error', reject); gate.listen(journal.control + '/gate.sock', resolve); });
    fs.chmodSync(journal.control + '/gate.sock', 0o600);
    const watcher = fs.watch(root + '/state', { persistent: false }, (event, name) => { if (name === path.basename(stateFile)) inspect(); });
    const poll = setInterval(inspect, 20);
    const child = spawn(runtime, ['--root=' + root + '/state', '--platform=systrap', '--host-uds=open', '--network=none', '--ignore-cgroups', 'run', '--bundle=' + root + '/bundle', id], { env: safeEnv, stdio: 'inherit' });
    const exited = new Promise(resolve => { child.once('error', () => resolve(1)); child.once('exit', code => resolve(code ?? 1)); });
    const birth = identity(child.pid);
    if (birth?.ppid === process.pid && birth.state !== 'Z') { journal.launcher = { pid: child.pid, start: birth.start }; save(); }
    else journal.uncertain = true;
    for (const signal of ['SIGTERM', 'SIGINT']) process.on(signal, () => child.kill('SIGKILL'));
    let exit = 1;
    try { exit = await exited; }
    finally { clearInterval(poll); watcher.close(); gate.close(); if (!await cleanup()) exit = 125; }
    process.exitCode = exit;
} else throw Error('Invalid protected runtime operation.');

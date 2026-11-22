// Offline acceptance: a real Codex tool turn, without provider credentials or model calls.
import fs from 'node:fs';
import http from 'node:http';
import { spawn } from 'node:child_process';
const [codex, run, profile] = process.argv.slice(2);
const readOnly = profile === 'read';
const work = run + '/work';
const home = run + '/home/codex-probe-' + profile;
fs.mkdirSync(home + '/.codex', { recursive: true });
let calls = 0;
const server = http.createServer(async (request, response) => {
    let raw = '';
    for await (const chunk of request) { raw += chunk; if (raw.length > 1048576) { response.writeHead(413); response.end(); return; } }
    const body = JSON.parse(raw);
    const tools = (body.tools ?? []).flatMap(tool => tool.type === 'namespace' ? (tool.tools ?? []).map(child => ({ ...child, namespace: tool.name })) : [tool]);
    let item;
    if (++calls === 1) {
        const tool = tools.find(tool => tool.name === 'exec_command') ?? tools.find(tool => tool.name === 'shell_command') ?? tools.find(tool => tool.name === 'shell');
        if (!tool) { response.writeHead(500); response.end(); return; }
        const source = work + '/aci-probe-source';
        const command = readOnly
            ? `if printf forbidden > '${source}'; then exit 99; fi; test "$(cat '${source}')" = original; readlink /proc/self/exe`
            : `printf changed > '${work}/aci-probe-result'; readlink /proc/self/exe`;
        const args = tool.name === 'exec_command' ? { cmd: command, yield_time_ms: 1000, max_output_tokens: 1000 }
            : tool.name === 'shell_command' ? { command } : { command: ['/bin/sh', '-c', command] };
        item = { type: 'function_call', id: 'fc_probe', call_id: 'call_probe', name: tool.name, arguments: JSON.stringify(args), ...(tool.namespace ? { namespace: tool.namespace } : {}) };
    } else item = { type: 'message', id: 'msg_probe', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: 'aci-probe-complete', annotations: [] }] };
    const result = { id: 'resp_probe_' + calls, object: 'response', created_at: 1, model: 'fixture', status: 'completed', output: [item], usage: { input_tokens: 1, output_tokens: 1, total_tokens: 2 } };
    response.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' });
    const emit = (type, data) => response.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`);
    emit('response.created', { response: { ...result, status: 'in_progress', output: [] } });
    emit('response.output_item.added', { output_index: 0, item: item.type === 'function_call' ? { ...item, arguments: '' } : item });
    if (item.type === 'function_call') emit('response.function_call_arguments.delta', { output_index: 0, item_id: item.id, delta: item.arguments });
    emit('response.output_item.done', { output_index: 0, item });
    emit('response.completed', { response: result });
    response.end();
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
fs.writeFileSync(home + '/.codex/config.toml', `model_provider="fixture"\nmodel="fixture"\n[model_providers.fixture]\nname="Offline probe"\nbase_url="http://127.0.0.1:${server.address().port}/v1"\nwire_api="responses"\nrequires_openai_auth=false\nrequest_max_retries=0\nstream_max_retries=0\n`);
const child = spawn(codex, ['exec', '--sandbox', 'danger-full-access', '--skip-git-repo-check', '--json', '-c', 'features.plugins=false', '-c', 'features.apps=false', 'Run the fixture command and finish.'],
    { cwd: work, env: { PATH: '/usr/local/bin:/usr/bin:/bin', HOME: home, LC_ALL: 'C' }, stdio: ['ignore', 'pipe', 'pipe'] });
let output = '';
child.stdout.on('data', chunk => output += chunk);
child.stderr.resume();
const exited = new Promise(resolve => { child.once('error', () => resolve(1)); child.once('exit', code => resolve(code ?? 1)); });
const timer = setTimeout(() => child.kill('SIGKILL'), 15000);
const exit = await exited;
clearTimeout(timer);
await new Promise(resolve => server.close(resolve));
const records = output.split('\n').flatMap(line => { try { return [JSON.parse(line)]; } catch { return []; } });
const toolSucceeded = records.some(record => record.type === 'item.completed' && record.item?.type === 'command_execution' && record.item.exit_code === 0);
const unchanged = !readOnly || fs.readFileSync(work + '/aci-probe-source', 'utf8') === 'original';
const changed = readOnly || (fs.existsSync(work + '/aci-probe-result') && fs.readFileSync(work + '/aci-probe-result', 'utf8') === 'changed');
const ok = exit === 0 && calls === 2 && toolSucceeded && unchanged && changed && output.includes('aci-probe-complete');
if (ok) console.log('aci-codex-ok');
process.exitCode = ok ? 0 : 1;

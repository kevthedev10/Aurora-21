#!/usr/bin/env node
// Only creates an owned disposable Docker PostgreSQL cluster; never reads a live connection.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
import {spawn, spawnSync} from 'node:child_process';
import {createInterface} from 'node:readline';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const packet=path.resolve(root,'../database-handoff');
const json=f=>JSON.parse(fs.readFileSync(f,'utf8'));
const spec=json(packet+'/implementation-specification.json');
const fixture=json(packet+'/validation-fixtures.json');
const s=fixture.symbols, ns='ultimateteam';
const sha=b=>crypto.createHash('sha256').update(b).digest('hex');
const quote=v=>"'"+String(v).replace(/'/g,"''")+"'";
const sqlValue=v=>v===null?'NULL':typeof v==='boolean'?(v?'TRUE':'FALSE'):typeof v==='number'?String(v):typeof v==='object'?quote(JSON.stringify(v))+'::json':quote(v);
const insert=(table,row)=>`INSERT INTO ${ns}.${table} (${Object.keys(row).join(',')}) VALUES (${Object.values(row).map(sqlValue).join(',')});`;
const fixtureRow=(table,id)=>fixture.tables.find(t=>t.name===table).rows.find(r=>r[spec.tables.find(t=>t.name===table).primaryKey]===id);
const copy=(table,id,changes)=>insert(table,{...fixtureRow(table,id),...changes});
const report={formatVersion:1,checkedAt:new Date().toISOString(),scope:'standalone fresh-schema artifacts; no runtime integration or real club changes',checks:[],database:{status:'not_run'},outsideScope:['C# runtime integration','existing-club migration','native FIFA identity verification','wire/API parity','live gameplay','performance tuning']};
let container, manifest, engineFatal;
const sessions=new Set();
const command=(args)=>{const r=spawnSync('docker',args,{encoding:'utf8',timeout:30000,maxBuffer:2**22});if(r.error||r.status!==0)throw new Error(r.error?.message||r.stderr.trim()||r.stdout.trim());return r.stdout.trim();};
async function check(id,fn){const start=Date.now();try{const details=await fn();report.checks.push({id,status:'PASS',elapsedMs:Date.now()-start,...details});console.log(id+': PASS');return details;}catch(e){report.checks.push({id,status:'FAIL',elapsedMs:Date.now()-start,error:e.message,sqlstate:e.sqlstate??null});console.log(id+': FAIL '+e.message);return null;}}
class Session{
 constructor(database){this.database=database;this.buffer=[];this.stderr='';this.pending=null;this.exited=false;
  this.child=spawn('docker',['exec','-i',container,'psql','-X','-qAt','-U','postgres','-d',database,'-v','ON_ERROR_STOP=1','-v','VERBOSITY=verbose'],{stdio:['pipe','pipe','pipe']});sessions.add(this);
  this.child.stdin.on('error',()=>{});
  createInterface({input:this.child.stdout}).on('line',line=>{if(this.pending&&line===this.pending.marker){const p=this.pending;this.pending=null;clearTimeout(p.timer);p.resolve(this.buffer.join('\n'));this.buffer=[];}else this.buffer.push(line);});
  this.child.stderr.on('data',b=>{this.stderr+=b.toString();});
  this.child.on('error',e=>this.abort(e));
  this.done=new Promise(resolve=>this.child.on('close',code=>{this.exited=true;sessions.delete(this);if(this.pending){const e=new Error(this.stderr.trim()||'psql exited '+code);e.sqlstate=this.stderr.match(/(?:ERROR|FATAL):\s+([A-Z0-9]{5}):/)?.[1];this.abort(e);}resolve(code);}));
 }
 abort(e){if(this.pending){const p=this.pending;this.pending=null;clearTimeout(p.timer);p.reject(e);}}
 query(sql,timeout=30000){assert(!this.pending,'Overlapping commands in one database session');if(this.exited)throw new Error('Session already exited');const marker='p21_'+crypto.randomUUID().replaceAll('-','');this.buffer=[];return new Promise((resolve,reject)=>{const timer=setTimeout(()=>{this.abort(new Error('Database command exceeded '+timeout+'ms'));this.child.kill('SIGTERM');},timeout);this.pending={marker,resolve,reject,timer};this.child.stdin.write(sql+`\nSELECT ${quote(marker)};\n`);});}
 async close(){if(!this.exited){this.child.stdin.end();await this.done;}}
}
async function withSession(db,fn){const c=new Session(db);try{return await fn(c);}finally{await c.close();}}
const query=(db,sql,timeout)=>withSession(db,c=>c.query(sql,timeout));
const data=(db,sql)=>query(db,`SELECT coalesce(json_agg(q),'[]'::json) FROM (${sql}) q;`).then(x=>JSON.parse(x));
async function createDB(db,template){await query('postgres',`CREATE DATABASE ${db}${template?' TEMPLATE '+template:''};`);}
async function dropDB(db){await query('postgres',`DROP DATABASE ${db};`);}
const ledgerDDL=()=>fs.readFileSync(root+'/schema/001-tables.sql','utf8').split('--BEGIN-LEDGER-DDL\n')[1].split('--END-LEDGER-DDL')[0];
async function acquire(c){await c.query('BEGIN; SET LOCAL standard_conforming_strings = on; SET LOCAL lock_timeout = \'30s\'; SET LOCAL statement_timeout = \'120s\'; SELECT pg_advisory_xact_lock(202100219901);',40000);}
async function installLocked(c,{hash=manifest.packageSha256,fault=false}={}){
 await c.query(`CREATE SCHEMA IF NOT EXISTS ${ns}; ${ledgerDDL()}`);
 const ledger=JSON.parse(await c.query(`SELECT coalesce(json_agg(q),'[]'::json) FROM (SELECT package_sha256 FROM ${ns}.setup_artifact_versions WHERE version_code=${quote(manifest.versionCode)}) q;`));
 if(ledger.length){assert.equal(ledger.length,1);if(ledger[0].package_sha256!==hash){await c.query('ROLLBACK;');throw new Error('Applied package hash mismatch');}await c.query('COMMIT;');return 'noop';}
 const tables=await c.query(`SELECT count(*) FROM information_schema.tables WHERE table_schema=${quote(ns)} AND table_name <> 'setup_artifact_versions';`);
 if(Number(tables)>0){await c.query('ROLLBACK;');throw new Error('Populated namespace without expected ledger');}
 for(const f of manifest.executionOrder){await c.query(fs.readFileSync(root+'/'+f,'utf8'),120000);if(fault&&f==='seed/013-player-definitions.sql')await c.query('SELECT 1/0;');}
 await c.query(`INSERT INTO ${ns}.setup_artifact_versions(version_code,package_sha256) VALUES(${quote(manifest.versionCode)},${quote(hash)}); COMMIT;`,120000);return 'installed';
}
const bootstrap=(db,opts)=>withSession(db,async c=>{await acquire(c);return installLocked(c,opts);});
async function expectedReject(db,sql,states=['23514','23503','23505']){try{await query(db,'BEGIN; SET LOCAL lock_timeout=\'30s\'; '+sql+' COMMIT;',40000);}catch(e){assert(states.includes(e.sqlstate),'Unexpected rejection: '+e.message);return {sqlstate:e.sqlstate};}throw new Error('Invalid transaction unexpectedly committed');}
async function expectError(fn,pattern){try{await fn();}catch(e){assert.match(e.message,pattern);return {errorObserved:e.message,sqlstate:e.sqlstate??null};}throw new Error('Expected error did not occur');}
const counts=async db=>{const rs={};for(const t of spec.tables)rs[t.name]=Number(await query(db,`SELECT count(*) FROM ${ns}.${t.name};`));return rs;};
async function lockWait(c){const rows=await data('postgres',`SELECT count(*) AS n FROM pg_stat_activity WHERE datname=${quote(c.database)} AND wait_event_type='Lock'`);assert(Number(rows[0].n)>0,'Expected contested lock was not observed');}
async function observeWait(c){const until=Date.now()+5000;while(Date.now()<until){const rows=await data('postgres',`SELECT count(*) AS n FROM pg_stat_activity WHERE datname=${quote(c.database)} AND wait_event_type='Lock'`);if(Number(rows[0].n)>0)return;await new Promise(r=>setTimeout(r,50));}await lockWait(c);}
async function fileChecks(){
 await check('F01',async()=>{for(const f of json(packet+'/execution-input-manifest.json').files){const b=fs.readFileSync(packet+'/'+f.path);assert.equal(b.length,f.bytes);assert.equal(sha(b),f.sha256);}return {packetHashes:16};});
 await check('F02',async()=>{const byTable=new Map(),files=json(packet+'/normalized-seed-manifest.json').files;for(const f of files){const b=fs.readFileSync(packet+'/'+f.path);assert.equal(sha(b),f.sha256);assert.equal(b.length,f.bytes);for(const t of JSON.parse(b).tables){const ts=spec.tables.find(x=>x.name===t.name);assert.deepEqual(t.columns,ts.columns.map(c=>c.name));const rows=byTable.get(t.name)??[];for(const row of t.rows){assert.deepEqual(Object.keys(row),t.columns);for(const c of ts.columns){const v=row[c.name];assert(c.nullable||v!==null);if(v!==null&&['bigint','smallint','integer'].includes(c.type))assert(Number.isSafeInteger(v));if(v!==null&&c.type==='boolean')assert.equal(typeof v,'boolean');}rows.push(row);}byTable.set(t.name,rows);}}
  const ids=new Map();for(const[t,rows]of byTable){const pk=spec.tables.find(x=>x.name===t).primaryKey;const ix=new Map(rows.map(r=>[r[pk],r]));assert.equal(ix.size,rows.length);ids.set(t,ix);}let tuples=0;for(const f of files){const doc=json(packet+'/'+f.path),sql=fs.readFileSync(root+'/seed/'+doc.seedFile,'utf8');let cursor=0;for(const t of doc.tables)for(const r of t.rows){for(const c of spec.tables.find(x=>x.name===t.name).columns)if(c.references&&r[c.name]!==null)assert(ids.get(c.references.table)?.has(r[c.name]));const tuple='('+t.columns.map(k=>sqlValue(r[k])).join(', ')+')';const pos=sql.indexOf(tuple,cursor);assert(pos>=0,'Missing/reordered seed tuple '+t.name);cursor=pos+tuple.length;tuples++;}}for(const u of spec.uniqueConstraints){const seen=new Set();for(const r of byTable.get(u.table)??[]){if(u.columns.some(c=>r[c]===null))continue;const key=JSON.stringify(u.columns.map(c=>r[c]));assert(!seen.has(key));seen.add(key);}}
  for(const t of ['clubs','items','squads','match_settlements','unopened_packs'])assert.equal(byTable.get(t)?.length??0,0);return {tuples};});
 await check('F03',async()=>{const files=['manifest.json','README.md',...manifest.executionOrder];const before=files.map(f=>sha(fs.readFileSync(root+'/'+f)));for(let i=0;i<2;i++){const p=spawnSync(process.execPath,[root+'/tools/generate.mjs'],{cwd:path.dirname(root),encoding:'utf8',timeout:30000});assert.equal(p.status,0,p.stderr);assert.deepEqual(files.map(f=>sha(fs.readFileSync(root+'/'+f))),before);}return {regenerations:2,foreignCwd:true};});
 await check('F04',async()=>{for(const f of manifest.files){const b=fs.readFileSync(root+'/'+f.path);assert.equal(sha(b),f.sha256);assert.equal(b.length,f.bytes);}assert.equal(manifest.packageSha256,sha(JSON.stringify(manifest.files.map(({path,sha256})=>({path,sha256})))));assert.deepEqual(manifest.seedTableCounts,json(packet+'/normalized-seed-manifest.json').rows);return {sqlHashes:manifest.files.length};});
}
async function databaseChecks(){
 const fresh='p21_fresh';await createDB(fresh);
 const b01=await check('B01',async()=>{assert.equal(await bootstrap(fresh),'installed');const version=Number(await query(fresh,'SHOW server_version_num;'));assert.equal(Math.floor(version/10000),18);report.database.serverVersionNum=version;const actual=await counts(fresh);for(const t of spec.tables)assert.equal(actual[t.name],manifest.seedTableCounts[t.name]??0);assert.equal(Number(await query(fresh,`SELECT count(*) FROM ${ns}.setup_artifact_versions;`)),1);const structural=await data(fresh,`SELECT contype,count(*)::int AS n FROM pg_constraint WHERE connamespace=${quote(ns)}::regnamespace GROUP BY contype`);const n=Object.fromEntries(structural.map(r=>[r.contype,r.n]));assert.equal(n.p,67);assert.equal(n.f,106);assert.equal(n.u,44);assert.equal(n.c,18);assert.equal(Number(await query(fresh,`SELECT count(*) FROM pg_indexes WHERE schemaname=${quote(ns)} AND indexname LIKE 'lfv4_idx_%';`)),64);const ix=await data(fresh,`SELECT c.relname AS name,i.indisunique AS unique,pg_get_expr(i.indpred,i.indrelid) AS predicate FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=${quote(ns)} AND c.relname LIKE 'lfv4_idx_%'`);for(const expected of spec.indexes){const got=ix.find(i=>i.name===expected.name);assert(got);assert.equal(got.unique,!!expected.unique);assert.equal(got.predicate!==null,!!expected.predicate);}assert.equal(Number(await query(fresh,`SELECT count(*) FROM pg_constraint WHERE connamespace=${quote(ns)}::regnamespace AND contype='f' AND (array_length(conkey,1)<>1 OR array_length(confkey,1)<>1 OR NOT condeferrable OR NOT condeferred);`)),0);return {tableCounts:actual,structure:n};});
 if(!b01)throw new Error('Fresh bootstrap failed; remaining database checks cannot run');
 await check('B02',async()=>{const before=await query(fresh,`SELECT json_agg(q ORDER BY sequencename) FROM (SELECT * FROM pg_sequences WHERE schemaname=${quote(ns)}) q;`);assert.equal(await bootstrap(fresh),'noop');assert.equal(await query(fresh,`SELECT json_agg(q ORDER BY sequencename) FROM (SELECT * FROM pg_sequences WHERE schemaname=${quote(ns)}) q;`),before);return {unchangedSequences:true};});
 await check('B03',()=>expectError(()=>bootstrap(fresh,{hash:'0'.repeat(64)}),/hash mismatch/));
 await createDB('p21_fault');await check('B04',async()=>{const e=await expectError(()=>bootstrap('p21_fault',{fault:true}),/division by zero/);assert.equal(Number(await query('p21_fault',`SELECT count(*) FROM pg_namespace WHERE nspname=${quote(ns)};`)),0);return e;});
 await check('B05',async()=>{assert.equal(await bootstrap('p21_fault'),'installed');return {};});
 await createDB('p21_race');await check('B06',async()=>{const a=new Session('p21_race'),b=new Session('p21_race');try{await acquire(a);const waiting=acquire(b);await observeWait(b);assert.equal(await installLocked(a),'installed');await waiting;assert.equal(await installLocked(b),'noop');return {observedLockWait:true};}finally{await a.close();await b.close();}});
 await createDB('p21_populated');await query('p21_populated',`CREATE SCHEMA ${ns}; CREATE TABLE ${ns}.dummy(id int);`);await check('B07',async()=>{const e=await expectError(()=>bootstrap('p21_populated'),/Populated namespace/);assert.equal(await query('p21_populated',`SELECT to_regclass('${ns}.dummy') IS NOT NULL;`),'t');return e;});
 await check('B08',()=>withSession(fresh,async c=>{const max=Number(await c.query(`SELECT max(footballer_id) FROM ${ns}.footballers;`));await c.query('BEGIN;');const id=Number(await c.query(insert('footballers',fixture.extraFootballer).slice(0,-1)+' RETURNING footballer_id;'));assert(id>max);await c.query('ROLLBACK;');return {generatedId:id,seededMax:max};}));
 await createDB('p21_fixture',fresh);await query('p21_fixture','BEGIN; '+fixture.tables.flatMap(t=>t.rows.map(r=>insert(t.name,r))).join('\n')+' COMMIT;',120000);
 const copiedItem=copy('items',s.playerA1,{id:900009001,native_item_id:50});const copiedPlayer=copy('player_items',s.playerA1,{item_id:900009001});
 const member=(changes={})=>copy('squad_players',s.membershipA,{id:900009002,slot_index:1,...changes});
 const other=()=>member({item_id:s.playerAOther,definition_id:s.definitionOther,footballer_id:s.footballerOther});
 const style=v=>`UPDATE ${ns}.player_items SET style_definition_id=${v} WHERE item_id=${s.playerA1};`;
 const binding=(club,slot)=>insert('club_slot_bindings',{id:900009014,club_id:club,slot_id:slot,reference_kind:'owned',item_id:s.kitA,definition_id:s.kitDefinition});
 const stat=(index,value,id=900009010)=>insert('player_stat_values',{id,item_id:s.playerA1,scope:'current',stat_index:index,value});
 const cases=[
 ['D01',null,true],['D02',copiedItem,false],['D03',copiedItem+insert('manager_items',{item_id:900009001,contracts:7}),false],['D04',copiedItem+copiedPlayer+insert('manager_items',{item_id:900009001,contracts:7}),false],['D05',copiedPlayer+copiedItem,true],
 ['D06',member({item_id:s.playerB,definition_id:s.definitionOther,footballer_id:s.footballerOther}),false],['D07',member({item_id:s.playerA2,definition_id:s.definitionA2}),false],['D08',other(),true],['D09',member({slot_index:0,item_id:s.playerAOther,definition_id:s.definitionOther,footballer_id:s.footballerOther}),false],['D10',member({definition_id:s.definitionA2,item_id:null,reference_kind:'concept',concept_wire_id:990000001}),false],
 ['D11',`UPDATE ${ns}.items SET club_id=${s.clubB} WHERE id=${s.playerA1};`,false],['D12',`UPDATE ${ns}.items SET definition_id=${s.definitionA2} WHERE id=${s.playerA1};`,false],['D13',`UPDATE ${ns}.squads SET club_id=${s.clubB} WHERE id=${s.squadA};`,false],['D14',`UPDATE ${ns}.squad_players SET footballer_id=${s.footballerOther} WHERE id=${s.membershipA};`,false],['D15',`UPDATE ${ns}.consumable_items SET quantity=2,untradeable_quantity=3 WHERE item_id=${s.consumableA};`,false],['D16',stat(5,0),false],['D17',stat(0,-1),false],['D18',stat(0,1)+stat(0,1,900009011),false],['D19',style(s.modifierDefinition),false],['D20',style(s.styleDefinition),true],
 ['D21',insert('squad_roles',{id:900009012,squad_id:s.squadA,role_code:'captain',ordinal:0,squad_player_id:s.membershipB}),false],['D22',insert('squad_managers',{id:900009013,squad_id:s.squadB,reference_kind:'owned',item_id:s.managerA,definition_id:s.managerDefinition}),false],['D23',binding(s.clubB,3),false],['D24',binding(s.clubA,3),true],['D25',binding(s.clubA,1),false],['D26',`UPDATE ${ns}.market_player_stat_values SET value=2 WHERE id=${s.snapshotStat};`,false],['D27',insert('market_player_stat_values',{id:900009015,trade_id:s.trade,scope:'current',stat_index:1,value:2}),false],['D28',`DELETE FROM ${ns}.items WHERE id=${s.playerA2};`,false],['D29',`UPDATE ${ns}.market_listings SET state='closed' WHERE trade_id=${s.trade}; DELETE FROM ${ns}.items WHERE id=${s.playerA2};`,true],['D30',`UPDATE ${ns}.market_listings SET definition_id=${s.definitionA1} WHERE trade_id=${s.trade};`,false],['D31',`UPDATE ${ns}.player_card_definitions SET rating=rating+1 WHERE definition_id=${s.definitionA1};`,false],['D32',`DELETE FROM ${ns}.items WHERE id=${s.playerA1};`,false],['D33',`UPDATE ${ns}.squads SET active=true WHERE id=${s.squadA};`+copy('squads',s.squadA,{id:900009020,native_squad_id:2,active:true}),false],['D34',other()+`UPDATE ${ns}.squad_players SET slot_index=1 WHERE id=${s.membershipA}; UPDATE ${ns}.squad_players SET slot_index=0 WHERE id=900009002;`,true],
 // Regressions beyond the prescribed checks: protect the bypasses discovered in review.
 ['R01',`UPDATE ${ns}.market_item_snapshots SET creation_transaction_id=pg_current_xact_id(),owners=99 WHERE trade_id=${s.trade};`,false],['R02',`DELETE FROM ${ns}.market_player_stat_values WHERE id=${s.snapshotStat};`,false],['R03',`DELETE FROM ${ns}.market_player_snapshots WHERE trade_id=${s.trade};`,false],['R04',`DELETE FROM ${ns}.market_listings WHERE trade_id=${s.trade};`,true],['R05',`UPDATE ${ns}.player_items SET item_id=900099999 WHERE item_id=${s.playerA1};`,false]
 ];
 for(const[id,sql,success]of cases){const db='p21_'+id.toLowerCase();await createDB(db,'p21_fixture');await check(id,async()=>{if(!sql){const r=(await data(db,`SELECT i.club_id,i.definition_id,i.native_item_id FROM ${ns}.player_items p JOIN ${ns}.items i ON i.id=p.item_id WHERE p.item_id=${s.playerA1}`))[0];assert.deepEqual(r,{club_id:s.clubA,definition_id:s.definitionA1,native_item_id:1});return r;}if(!success)return expectedReject(db,sql);await query(db,'BEGIN; '+sql+' COMMIT;');if(id==='D29')assert.equal(Number(await query(db,`SELECT count(*) FROM ${ns}.market_item_snapshots WHERE trade_id=${s.trade};`)),1);if(id==='R04')assert.equal(Number(await query(db,`SELECT count(*) FROM ${ns}.market_player_stat_values WHERE trade_id=${s.trade};`)),0);return {committed:true};});await dropDB(db);}
 for(const id of ['C01','C02','C03']){const db='p21_'+id.toLowerCase();await createDB(db,'p21_fixture');await check(id,async()=>{const a=new Session(db),b=new Session(db);try{await a.query("BEGIN; SET LOCAL lock_timeout='30s';");await b.query("BEGIN; SET LOCAL lock_timeout='30s';");
  if(id==='C01'){await a.query(`DELETE FROM ${ns}.squad_players WHERE id=${s.membershipA}; COMMIT; BEGIN;`);await a.query(member({id:900010001,item_id:s.playerA1,definition_id:s.definitionA1,slot_index:1}));await b.query(member({id:900010002,item_id:s.playerA2,definition_id:s.definitionA2,slot_index:2}));}
  if(id==='C02'){await a.query(other());await b.query(`DELETE FROM ${ns}.items WHERE id=${s.playerAOther};`);}
  let pending;
  if(id==='C03'){await a.query(`UPDATE ${ns}.market_listings SET state='closed' WHERE trade_id=${s.trade}; DELETE FROM ${ns}.items WHERE id=${s.playerA2};`);const newTrade=900010003;const build=['market_listings','market_item_snapshots','market_player_snapshots','market_player_stat_values'].map(t=>copy(t,t==='market_player_stat_values'?s.snapshotStat:s.trade,{trade_id:newTrade,...(t==='market_player_stat_values'?{id:900010004}:{})})).join(' ');pending=b.query(build+' COMMIT;',40000).then(()=>({committed:true}),e=>({committed:false,sqlstate:e.sqlstate,error:e.message}));await observeWait(b);}
  const outcomes=id==='C03'?[await a.query('COMMIT;').then(()=>({committed:true})),await pending]:await Promise.all([a,b].map(c=>c.query('COMMIT;',40000).then(()=>({committed:true}),e=>({committed:false,sqlstate:e.sqlstate,error:e.message}))));
  const commits=outcomes.filter(o=>o.committed).length;if(id==='C01')assert.equal(commits,1);if(id==='C02'){assert(commits<=1);assert.equal(Number(await query(db,`SELECT count(*) FROM ${ns}.squad_players p LEFT JOIN ${ns}.items i ON i.id=p.item_id WHERE p.item_id=${s.playerAOther} AND i.id IS NULL;`)),0);}if(id==='C03')assert.equal(outcomes[1].committed,false);
  for(const o of outcomes)if(!o.committed)assert(['23514','23503','23505','40P01'].includes(o.sqlstate),o.error);return {independentSessions:true,outcomes};
 }finally{await a.close();await b.close();}});await dropDB(db);}
}
try{
 manifest=json(root+'/manifest.json');await fileChecks();if(report.checks.some(t=>t.status==='FAIL'))throw new Error('File checks failed');
 try{command(['version','--format','{{.Server.Version}}']);command(['image','inspect','postgres:18','--format','{{.Id}}']);}catch(e){report.database={status:'unavailable',reason:e.message};throw e;}
 const name='p21-db-validation-'+crypto.randomUUID();container=command(['run','-d','--name',name,'--label','aurora21.validation='+name,'-e','POSTGRES_PASSWORD='+crypto.randomBytes(24).toString('hex'),'-p','127.0.0.1::5432','postgres:18']);assert.match(container,/^[a-f0-9]{64}$/);
 report.database={status:'running',imageId:command(['image','inspect','postgres:18','--format','{{.Id}}'])};
 const until=Date.now()+30000;while(true){try{command(['exec',container,'pg_isready','-U','postgres']);break;}catch(e){if(Date.now()>until)throw e;await new Promise(r=>setTimeout(r,250));}}
 await databaseChecks();report.database.status='completed';
}catch(e){engineFatal=e.message;report.fatalError=e.message;console.error('validation: '+e.message);}
finally{
 for(const c of [...sessions])await c.close();
 if(container){try{command(['rm','-f',container]);report.database.ownedContainerRemoved=true;}catch(e){report.database.cleanupError=e.message;}}
 report.summary={pass:report.checks.filter(c=>c.status==='PASS').length,fail:report.checks.filter(c=>c.status==='FAIL').length};report.status=!engineFatal&&report.summary.fail===0?'PASS':report.database.status==='unavailable'?'INCOMPLETE':'FAIL';
 fs.mkdirSync(root+'/validation',{recursive:true});fs.writeFileSync(root+'/validation/results.json',JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify(report.summary));if(report.status!=='PASS')process.exitCode=1;
}

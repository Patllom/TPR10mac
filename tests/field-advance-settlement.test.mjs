import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { loadTs } from './helpers/load-ts.mjs';
const view = existsSync('lib/field-advances/settlement-view.ts') ? loadTs('lib/field-advances/settlement-view.ts') : {};
test('pending command freezes original payload and rejects replacement until explicit receipt', () => {
 assert.equal(typeof view.createPendingStore, 'function');
 const store=view.createPendingStore();
 const body={lines:[{amount:'12.00'}]}, meta={commandId:'original',requestVersion:1,familyVersion:2,entityVersion:3,reason:'เหตุผล'};
 store.begin('account','request','rounds/update',body,meta);
 body.lines[0].amount='99';meta.commandId='changed';
 assert.equal(store.current('account','request').body.lines[0].amount,'12.00');
 assert.equal(store.current('account','request').command.commandId,'original');
 assert.throws(()=>store.begin('account','request','rounds/update',{},meta));
 assert.equal(store.current('other','request'),null);
 store.resolve('account','request','wrong');assert.ok(store.current('account','request'));
 store.resolve('account','request','original');assert.equal(store.current('account','request'),null);
});
test('money previews reject excess precision and round each line half up without Number loss', () => {
 assert.equal(typeof view.linePreview,'function');
 assert.equal(view.linePreview('1.0001','50.00',null),'50.01');
 assert.equal(view.linePreview(null,null,'9999999999999999.99'),'9999999999999999.99');
 assert.equal(view.linePreview('1.00001','50',null),null);
 assert.equal(view.linePreview(null,null,'1.001'),null);
});

test('editing a line preserves all allocations and its source reference',()=>{
 assert.equal(typeof view.editableLine,'function');
 const source={sourceLineId:'source',categoryId:'category',description:'เดิม',incurredOn:'2026-10-31',quantity:'1',rate:'2',lumpSum:null,allocations:[{documentId:'one',amount:'1'},{documentId:'two',amount:'1'}]};
 const edited=view.editableLine(source);
 assert.deepEqual(edited.allocations,source.allocations);assert.equal(edited.sourceLineId,'source');
 edited.allocations[0].amount='2';assert.equal(source.allocations[0].amount,'1');
});
test('protected data reset may hide pending without replacing it; identity change explicitly destroys it',()=>{
 assert.equal(typeof view.createPendingStore,'function');const store=view.createPendingStore();
 const command={commandId:'same',requestVersion:2,familyVersion:3,entityVersion:null,reason:'test'};
 store.begin('a','r','declaration',{complete:true},command);
 assert.ok(Object.isFrozen(store.current('a','r').body));
 assert.equal(store.current('a','r').command.commandId,'same');
 store.clear();assert.equal(store.current('a','r'),null);
});

test('document form prevents known invalid drafts before financial dispatch',()=>{
 assert.equal(typeof view.documentInputError,'function');
 const good={evidenceType:'ใบเสร็จ',issuer:'ร้าน',number:null,hasNoNumber:true,issuedOn:'2026-10-31',amount:'10.00',evidenceIds:['proof']};
 assert.equal(view.documentInputError(good,'เหตุผล'),null);
 for(const [patch,reason] of [[{amount:'1.001'},'เหตุผล'],[{hasNoNumber:false},'เหตุผล'],[{evidenceIds:[]},'เหตุผล'],[{},'']])assert.ok(view.documentInputError({...good,...patch},reason));
});

test('R1 rejects zero variants and storage bounds, preserving explicit adjustment zero',()=>{
 for(const value of ['0','00.00','000','10000000000000000','1.001','-1']) {
  assert.equal(view.linePreview(null,null,value),null,value);
  assert.ok(view.documentInputError({issuer:'ร้าน',number:null,hasNoNumber:true,issuedOn:'2026-10-31',amount:value,evidenceIds:['proof']},'เหตุผล'));
 }
 assert.equal(view.linePreview('0','1',null),null);
 assert.equal(view.linePreview('10000000000000000','1',null),null);
 assert.equal(view.linePreview('9999999999999999','2',null),null);
 assert.equal(view.linePreview('0','1',null,true),'0.00');
 assert.equal(view.linePreview(null,null,'00.00',true),'0.00');
 assert.equal(view.linePreview('1','0',null,true),null);
});
test('R1 validates allocations before pending creation',()=>{
 assert.equal(typeof view.roundInputError,'function');
 const good={sourceLineId:null,categoryId:'cat',description:'expense',incurredOn:'2026-10-31',quantity:'1',rate:'10',lumpSum:null,allocations:[{documentId:'doc',amount:'10'}]};
 assert.equal(view.roundInputError([good]),null);
 for(const allocations of [[{documentId:'doc',amount:'00.00'}],[{documentId:'doc',amount:'11'}],[{documentId:'doc',amount:'1'},{documentId:'doc',amount:'1'}]])assert.ok(view.roundInputError([{...good,allocations}]));
 assert.ok(view.roundInputError([{...good,quantity:'0'}]));
 assert.equal(view.roundInputError([{...good,sourceLineId:'old',quantity:'0',allocations:[]}],true),null);
});

function storageFixture(){const values=new Map();return {getItem:k=>values.get(k)??null,setItem:(k,v)=>values.set(k,v),removeItem:k=>values.delete(k),values};}
test('R2 full reload keeps identity only and blocks replacement until matching receipt',()=>{
 const storage=storageFixture(),store=view.createPendingStore(()=>storage);
 store.begin('a','r','rounds/update',{entityId:'round',lines:[{amount:'secret-money'}]},{commandId:'original',requestVersion:1,familyVersion:1,entityVersion:1,reason:'secret-reason'});
 const persisted=[...storage.values.values()].join('');assert.ok(persisted.includes('original'));assert.ok(!persisted.includes('secret'));assert.ok(!persisted.includes('requestVersion'));
 const reloaded=view.createPendingStore(()=>storage),p=reloaded.current('a','r');
 assert.equal(p.command.commandId,'original');assert.equal(p.body,undefined);assert.equal(p.entityId,'round');
 assert.throws(()=>reloaded.begin('a','r','rounds/create',{}, {commandId:'replacement'}));
 assert.equal(reloaded.current('other','r'),null);
 reloaded.resolve('a','r','wrong');assert.ok(reloaded.current('a','r'));
 reloaded.resolve('a','r','original');assert.equal(reloaded.current('a','r'),null);
});
test('R2 storage failure prevents begin and account change clears payload but retains unresolved identity',()=>{
 const broken={getItem:()=>null,setItem:()=>{throw Error('storage denied')},removeItem:()=>{}};
 const store=view.createPendingStore(()=>broken);
 assert.throws(()=>store.begin('a','r','declaration',{complete:true},{commandId:'c'}));assert.equal(store.current('a','r'),null);
 const storage=storageFixture(),good=view.createPendingStore(()=>storage);
 good.begin('a','r','declaration',{complete:true},{commandId:'c',reason:'private'});good.clear();
 assert.equal(good.current('other','r'),null);assert.equal(good.current('a','r').body,undefined);assert.equal(good.current('a','r').command.commandId,'c');
});

test('document business evidence kind must be selected, never inferred from a file',()=>{
 const input={issuer:'ร้าน',number:null,hasNoNumber:true,issuedOn:'2026-10-31',amount:'10.00',evidenceIds:['proof']};
 assert.ok(view.documentInputError(input,'เหตุผล'));
 assert.ok(view.documentInputError({...input,evidenceType:'  '},'เหตุผล'));
 assert.equal(view.documentInputError({...input,evidenceType:'ใบเสร็จ'},'เหตุผล'),null);
});

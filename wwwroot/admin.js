const state={status:null,charts:[],published:[],selected:null};
const $=id=>document.getElementById(id);

async function request(url,options={}){
  const response=await fetch(url,{...options,headers:{'Content-Type':'application/json',...(options.headers||{})}});
  const body=await response.json().catch(()=>null);
  if(!response.ok)throw new Error(body?.error||`Request failed (${response.status})`);
  return body;
}

function showMessage(text='',kind=''){$('message').textContent=text;$('message').className=`message ${kind}`;}

async function load(){
  showMessage('Scanning screenshot folder…');
  try{
    [state.status,state.charts]=await Promise.all([request('/api/status'),request('/api/charts/pending')]);
    state.published=await request('/api/charts/published');
    renderTarget();renderQueue();renderExisting();
    if(state.selected){state.selected=state.charts.find(c=>c.tickerSymbol===state.selected.tickerSymbol)||state.charts[0];}
    else state.selected=state.charts[0];
    renderForm();showMessage(`${state.charts.length} changed screenshot${state.charts.length===1?'':'s'} found.`);
  }catch(error){showMessage(error.message,'error');}
}

function renderExisting(){
  const active=new Set(state.charts.map(chart=>chart.tickerSymbol));
  const available=state.published.filter(chart=>!active.has(chart.tickerSymbol));
  const select=$('existingChart'),previous=select.value;
  select.replaceChildren(...available.map(chart=>{const option=document.createElement('option');option.value=chart.tickerSymbol;option.textContent=`${chart.tickerSymbol} — ${chart.companyName}`;return option;}));
  if(available.some(chart=>chart.tickerSymbol===previous))select.value=previous;
  $('reopenChart').disabled=available.length===0;
  if(available.length===0){const option=document.createElement('option');option.textContent='No additional published charts';option.value='';select.append(option);}
}

function renderTarget(){
  const live=String(state.status.target).toLowerCase()==='live';
  $('targetSwitch').checked=live;$('targetSwitch').disabled=false;
  $('targetBadge').textContent=live?'Live Azure':'Local Preview';$('targetBadge').className=`badge ${live?'live':'local'}`;
  $('publish').textContent=live?'Publish to LIVE site':'Publish to local preview';$('publish').disabled=live&&!state.status.allowLivePublishing;
}

function renderQueue(){
  $('pendingCount').textContent=state.charts.filter(c=>!c.ready&&!c.skipped).length;
  $('readyCount').textContent=state.charts.filter(c=>c.ready&&!c.skipped).length;
  $('skippedCount').textContent=state.charts.filter(c=>c.skipped).length;
  $('empty').hidden=state.charts.length!==0;$('workspace').hidden=state.charts.length===0;
  $('queue').replaceChildren(...state.charts.map(chart=>{
    const button=document.createElement('button');button.className=`queue-item ${state.selected?.tickerSymbol===chart.tickerSymbol?'active':''}`;
    const strong=document.createElement('strong');strong.textContent=chart.tickerSymbol;
    const dot=document.createElement('span');dot.className=`dot ${chart.ready?'ready':chart.skipped?'skipped':''}`;
    const small=document.createElement('small');small.textContent=chart.companyName;
    button.append(strong,dot,small);button.onclick=()=>{state.selected=chart;renderQueue();renderForm();};return button;
  }));
}

function renderForm(){
  const chart=state.selected;if(!chart)return;
  $('formTicker').textContent=chart.tickerSymbol;$('ticker').value=chart.tickerSymbol;$('company').value=chart.companyName;
  const currency=normalizeCurrency(chart.currency,chart.tickerSymbol);chart.currency=currency;
  chart.currencyTickerSymbols=chart.currencyTickerSymbols||{};if(!chart.currencyTickerSymbols[currency])chart.currencyTickerSymbols[currency]=chart.tickerSymbol;
  chart.currencyProviderSymbols=chart.currencyProviderSymbols||{};
  $('chartCurrency').value=currency;$('exchange').value=chart.exchange??'';$('usdTicker').value=chart.currencyTickerSymbols.USD??'';$('cadTicker').value=chart.currencyTickerSymbols.CAD??'';
  $('upperLine').value=chart.upperLine??'';$('lowerLine').value=chart.lowerLine??'';$('comments').value=chart.comments??'';
  loadProviderSymbols(chart,currency);$('currencyNote').textContent='Changing currency converts both chart boundaries using the latest Bank of Canada daily reference rate.';
  const imageEndpoint=chart.useEditedImage&&chart.editedImagePath?'edited-image':'source-image';
  $('chartImage').src=`/api/${imageEndpoint}/${encodeURIComponent(chart.tickerSymbol)}?h=${imageEndpoint==='edited-image'?chart.editedImageHash:chart.imageHash}`;
  $('analyzeLines').disabled=!state.status.openAiEnabled;$('removeVideo').disabled=!state.status.openAiEnabled;
  $('analyzeLines').title=state.status.openAiEnabled?'Ask OpenAI to suggest the two numeric boundaries.':'Configure OpenAI:ApiKey to enable this action.';
  $('removeVideo').title=state.status.openAiEnabled?'Ask OpenAI to produce an edited candidate image.':'Configure OpenAI:ApiKey to enable this action.';
  const analysis=chart.analysis;
  $('analysisResult').hidden=!analysis;
  if(analysis){
    const confidence=analysis.confidence==null?'':` · ${Math.round(Number(analysis.confidence)*100)}% confidence`;
    const lines=analysis.suggestedUpperLine!=null&&analysis.suggestedLowerLine!=null?`Suggested upper ${analysis.suggestedUpperLine}, lower ${analysis.suggestedLowerLine}${confidence}. `:'';
    $('analysisResult').textContent=`${lines}${analysis.explanation||'OpenAI could not determine the chart boundaries.'}`;
    $('analysisResult').className=`analysis-result ${analysis.status==='suggested'?'':'error'}`;
  }
  $('imageChoice').hidden=!chart.editedImagePath;
  $('showOriginal').classList.toggle('selected',!chart.useEditedImage);$('useEdited').classList.toggle('selected',chart.useEditedImage);
  $('saveState').textContent=chart.ready?'Ready':chart.skipped?'Skipped':'Draft';
}

function formValue(ready=false,skipped=false){
  const number=id=>$(id).value===''?null:Number($(id).value);
  const currency=$('chartCurrency').value;stashProviderSymbols(state.selected,currency);
  const currencyTickerSymbols={...(state.selected.currencyTickerSymbols||{}),USD:$('usdTicker').value.trim().toUpperCase(),CAD:$('cadTicker').value.trim().toUpperCase()};
  const providerSymbols=state.selected.currencyProviderSymbols?.[currency]||{};
  return {...state.selected,tickerSymbol:$('ticker').value.trim().toUpperCase(),companyName:$('company').value.trim(),currency,exchange:$('exchange').value.trim().toUpperCase()||null,currencyTickerSymbols,upperLine:number('upperLine'),lowerLine:number('lowerLine'),comments:$('comments').value.trim(),providerSymbols,currencyProviderSymbols:state.selected.currencyProviderSymbols,ready,skipped};
}

function normalizeCurrency(value,ticker){const normalized=String(value||'').toUpperCase();if(normalized==='CAD'||normalized==='USD')return normalized;return /\.(V|TO)$/i.test(ticker)?'CAD':'USD';}
function stashProviderSymbols(chart,currency){chart.currencyProviderSymbols=chart.currencyProviderSymbols||{};chart.currencyProviderSymbols[currency]={twelveData:$('twelveSymbol').value.trim(),finnhub:$('finnhubSymbol').value.trim()};}
function loadProviderSymbols(chart,currency){const mapped=chart.currencyProviderSymbols?.[currency]||(normalizeCurrency(chart.currency,chart.tickerSymbol)===currency?chart.providerSymbols:null)||{};$('twelveSymbol').value=mapped.twelveData??'';$('finnhubSymbol').value=mapped.finnhub??'';}
function converted(value,rate){if(value==='')return '';const result=Number(value)*Number(rate);return Number.isFinite(result)?String(Number(result.toFixed(6))):value;}

$('chartCurrency').addEventListener('change',async event=>{
  const chart=state.selected;if(!chart)return;
  const from=normalizeCurrency(chart.currency,chart.tickerSymbol),to=event.target.value;
  stashProviderSymbols(chart,from);chart.currencyTickerSymbols={...(chart.currencyTickerSymbols||{}),USD:$('usdTicker').value.trim().toUpperCase(),CAD:$('cadTicker').value.trim().toUpperCase()};
  if(from===to){loadProviderSymbols(chart,to);return;}
  event.target.disabled=true;$('currencyNote').textContent=`Converting ${from} to ${to}…`;
  try{
    const rate=await request(`/api/fx?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`);
    $('upperLine').value=converted($('upperLine').value,rate.rate);$('lowerLine').value=converted($('lowerLine').value,rate.rate);
    chart.currency=to;loadProviderSymbols(chart,to);
    $('currencyNote').textContent=`Converted at 1 ${from} = ${Number(rate.rate).toFixed(6)} ${to} · ${rate.provider} · ${rate.effectiveDate}. Review before saving.`;
    showMessage(`Chart currency changed to ${to}; both boundaries were converted.`,'success');
  }catch(error){event.target.value=from;$('currencyNote').textContent='Currency conversion failed; the boundaries were not changed.';showMessage(error.message,'error');}
  finally{event.target.disabled=false;}
});

async function save(ready=false,skipped=false){
  const original=state.selected.tickerSymbol;$('saveState').textContent='Saving…';
  try{
    const saved=await request(`/api/charts/${encodeURIComponent(original)}`,{method:'PUT',body:JSON.stringify(formValue(ready,skipped))});
    const index=state.charts.findIndex(c=>c.tickerSymbol===original);state.charts[index]=saved;state.selected=saved;renderQueue();renderForm();showMessage(`${saved.tickerSymbol} saved.`,'success');
  }catch(error){$('saveState').textContent='Not saved';showMessage(error.message,'error');}
}

$('reviewForm').addEventListener('submit',event=>{event.preventDefault();save(true,false);});
$('save').onclick=()=>save(false,false);$('skip').onclick=()=>save(false,true);$('rescan').onclick=load;
$('reopenChart').onclick=async()=>{
  const ticker=$('existingChart').value;if(!ticker)return;
  showMessage(`Opening ${ticker} for review…`);
  try{
    const draft=await request(`/api/charts/${encodeURIComponent(ticker)}/reopen`,{method:'POST'});
    state.charts.push(draft);state.selected=draft;renderQueue();renderExisting();renderForm();
    showMessage(`${ticker} opened with its currently published values. Update it and mark it ready to republish.`,'success');
  }catch(error){showMessage(error.message,'error');}
};
function openPreview(){
  const url=state.status?.previewUrl||'/preview/';
  const separator=url.includes('?')?'&':'?';
  window.open(`${url}${separator}refresh=${Date.now()}`,'risk-reward-preview');
}
$('openPreview').onclick=openPreview;
$('targetSwitch').addEventListener('change',async event=>{
  const target=event.target.checked?'live':'local';
  try{
    const result=await request('/api/target',{method:'PUT',body:JSON.stringify({target})});
    state.status.target=String(result.target).toLowerCase();renderTarget();showMessage(`Publishing target changed to ${target==='live'?'Live Azure':'local preview'}.`,'success');
  }catch(error){renderTarget();showMessage(error.message,'error');}
});
async function runAiAction(action,button,busyText){
  if(!state.selected)return;
  const ticker=state.selected.tickerSymbol;button.disabled=true;const previous=button.textContent;button.textContent=busyText;
  showMessage(`Sending ${ticker} to the OpenAI API…`);
  try{
    const saved=await request(`/api/charts/${encodeURIComponent(ticker)}/${action}`,{method:'POST'});
    const index=state.charts.findIndex(c=>c.tickerSymbol===ticker);state.charts[index]=saved;state.selected=saved;renderQueue();renderForm();
    showMessage(action==='analyze'?`OpenAI suggestions loaded for ${ticker}. Check the values before marking it ready.`:`Edited candidate loaded for ${ticker}. Compare it with the original before publishing.`,'success');
  }catch(error){showMessage(error.message,'error');}
  finally{button.textContent=previous;button.disabled=!state.status.openAiEnabled;}
}
async function chooseImage(useEditedImage){
  if(!state.selected)return;
  try{
    const ticker=state.selected.tickerSymbol;
    const saved=await request(`/api/charts/${encodeURIComponent(ticker)}/image-choice`,{method:'PUT',body:JSON.stringify({useEditedImage})});
    const index=state.charts.findIndex(c=>c.tickerSymbol===ticker);state.charts[index]=saved;state.selected=saved;renderForm();
    showMessage(`${useEditedImage?'Edited candidate':'Original screenshot'} selected for publication.`,'success');
  }catch(error){showMessage(error.message,'error');}
}
$('analyzeLines').onclick=()=>runAiAction('analyze',$('analyzeLines'),'Analyzing…');
$('removeVideo').onclick=()=>runAiAction('remove-video',$('removeVideo'),'Editing…');
$('showOriginal').onclick=()=>chooseImage(false);$('useEdited').onclick=()=>chooseImage(true);
$('publish').onclick=async()=>{
  const live=String(state.status.target).toLowerCase()==='live',destination=live?'Live Azure':'the local preview';
  showMessage(`Publishing approved charts to ${destination}…`);
  try{const result=await request('/api/publish',{method:'POST',body:'{}'});const confirmation=result.tickers.length?`Published ${result.tickers.length} chart(s) to ${destination}.`:`Published the current site assets and chart catalog to ${destination}.`;await load();showMessage(confirmation,'success');if(!live)openPreview();alert(confirmation);}catch(error){showMessage(error.message,'error');}
};
load();

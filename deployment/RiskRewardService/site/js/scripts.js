const app={charts:[],prices:{},priceGeneratedAt:null,selected:null,query:''};
const $=id=>document.getElementById(id);
const text=(tag,value,className)=>{const node=document.createElement(tag);node.textContent=value;if(className)node.className=className;return node;};
const money=(value,currency='USD')=>value==null?'—':new Intl.NumberFormat('en-US',{style:'currency',currency,maximumFractionDigits:value<10?3:2}).format(value);
const dateTime=value=>value?new Intl.DateTimeFormat('en-US',{dateStyle:'medium',timeStyle:'short'}).format(new Date(value)):'Unavailable';

function allocation(price,lower,upper){
  if(!(price>0&&lower>0&&upper>lower))return null;
  const raw=(Math.log(upper)-Math.log(price))/(Math.log(upper)-Math.log(lower))*10;
  return Math.max(0,Math.min(10,raw));
}

async function load(){
  try{
    const selectedTicker=app.selected?.tickerSymbol;
    const [chartResponse,priceResponse]=await Promise.all([fetch(`data.json?v=${Date.now()}`,{cache:'no-store'}),fetch(`prices.json?v=${Date.now()}`,{cache:'no-store'}).catch(()=>null)]);
    if(!chartResponse.ok)throw new Error('Chart catalog could not be loaded.');
    const catalog=await chartResponse.json();app.charts=normalizeCharts(catalog);
    if(priceResponse?.ok){const prices=await priceResponse.json();app.prices=prices.quotes||prices;app.priceGeneratedAt=prices.generatedAt??prices.GeneratedAt??null;renderMarketStatus(app.prices,app.priceGeneratedAt);}
    else{app.prices={};app.priceGeneratedAt=null;renderMarketStatus({},null);}
    app.selected=app.charts.find(chart=>chart.tickerSymbol===selectedTicker)||app.charts[0]||null;renderList();renderDetail();
  }catch(error){$('detail').replaceChildren(text('div',error.message,'empty-state'));}
}

function normalizeCharts(input){
  const rows=Array.isArray(input)?input:(input.charts||[]);
  return rows.map(row=>{const tickerSymbol=row.tickerSymbol??row.TickerSymbol??row['Ticker Symbol']??'',currency=String(row.currency??row.Currency??(/\.(V|TO)$/i.test(tickerSymbol)?'CAD':'USD')).toUpperCase(),currencyTickerSymbols=row.currencyTickerSymbols??row.CurrencyTickerSymbols??{},displayTickerSymbol=currencyTickerSymbols[currency]??currencyTickerSymbols[currency.toLowerCase()]??tickerSymbol;return {tickerSymbol,displayTickerSymbol,currency,currencyTickerSymbols,companyName:row.companyName??row.CompanyName??row['Company Name']??'',updatedDate:row.updatedDate??row.UpdatedDate??row['Updated Date'],chartFilename:row.chartFilename??row.ChartFilename??row['Chart Filename'],comments:row.comments??row.Comments??'',upperLine:Number(row.upperLine??row.UpperLine)||null,lowerLine:Number(row.lowerLine??row.LowerLine)||null};}).sort((a,b)=>a.displayTickerSymbol.localeCompare(b.displayTickerSymbol));
}

function quoteFor(chart){const quote=app.prices[chart.tickerSymbol]||app.prices[chart.tickerSymbol.toUpperCase()];if(!quote)return null;return {...quote,price:Number(quote.price??quote.Price),quotedAt:quote.quotedAt??quote.QuotedAt,provider:quote.provider??quote.Provider,isStale:quote.isStale??quote.IsStale,currency:quote.currency??quote.Currency??'USD'};}

function easternParts(value){
  return Object.fromEntries(new Intl.DateTimeFormat('en-US',{timeZone:'America/New_York',year:'numeric',month:'2-digit',day:'2-digit',weekday:'short',hour:'2-digit',minute:'2-digit',hourCycle:'h23'}).formatToParts(value).filter(part=>part.type!=='literal').map(part=>[part.type,part.value]));
}

function dateKey(parts){return `${parts.year}-${parts.month}-${parts.day}`;}
function previousWeekday(parts){
  let date=new Date(Date.UTC(Number(parts.year),Number(parts.month)-1,Number(parts.day)));
  do{date=new Date(date.getTime()-86400000);}while(date.getUTCDay()===0||date.getUTCDay()===6);
  return date.toISOString().slice(0,10);
}
function mostRecentRequiredClose(now=new Date()){
  const parts=easternParts(now),minutes=Number(parts.hour)*60+Number(parts.minute),weekend=parts.weekday==='Sat'||parts.weekday==='Sun';
  if(!weekend&&minutes>=16*60)return dateKey(parts);
  return previousWeekday(parts);
}
function hasRequiredCloseUpdate(generatedAt,now=new Date()){
  if(!generatedAt)return false;
  const generated=new Date(generatedAt);if(Number.isNaN(generated.getTime()))return false;
  const parts=easternParts(generated),generatedDate=dateKey(parts),requiredDate=mostRecentRequiredClose(now);
  return generatedDate>requiredDate||(generatedDate===requiredDate&&Number(parts.hour)*60+Number(parts.minute)>=16*60);
}

function renderMarketStatus(quotes,generatedAt){
  const pill=$('marketStatus'),values=Object.values(quotes||{});
  if(values.length===0){pill.textContent='Prices unavailable';pill.className='status-pill stale';return;}
  const times=values.map(quote=>new Date(quote.quotedAt??quote.QuotedAt??0).getTime()).filter(Number.isFinite);
  const newest=times.length?Math.max(...times):0;
  const explicitlyStale=values.every(quote=>(quote.isStale??quote.IsStale)===true);
  const now=new Date(),current=easternParts(now),minutes=Number(current.hour)*60+Number(current.minute),weekday=current.weekday!=='Sat'&&current.weekday!=='Sun',marketOpen=weekday&&minutes>=9*60+30&&minutes<16*60;
  const stale=explicitlyStale||(marketOpen?(!newest||(Date.now()-newest)/60000>30):!hasRequiredCloseUpdate(generatedAt,now));
  if(!marketOpen&&!stale){pill.textContent='Market closed';pill.className='status-pill closed';return;}
  pill.textContent=stale?'Prices may be stale':'Prices current';pill.className=`status-pill ${stale?'stale':'fresh'}`;
}

function renderList(){
  const filtered=app.charts.filter(c=>`${c.tickerSymbol} ${c.displayTickerSymbol} ${c.companyName}`.toLowerCase().includes(app.query));$('resultCount').textContent=`${filtered.length} chart${filtered.length===1?'':'s'}`;
  $('chartList').replaceChildren(...filtered.map(chart=>{
    const quote=quoteFor(chart),value=allocation(quote?.price,chart.lowerLine,chart.upperLine);
    const button=document.createElement('button');button.className=`chart-row ${app.selected===chart?'active':''}`;button.setAttribute('role','option');button.setAttribute('aria-selected',app.selected===chart);
    const copy=document.createElement('span');copy.className='row-copy';copy.append(text('strong',chart.displayTickerSymbol),text('small',chart.companyName));
    button.append(text('span',chart.displayTickerSymbol.slice(0,4),'ticker-mark'),copy,text('span',value==null?'—':`${value.toFixed(1)}%`,'allocation-chip'));
    button.onclick=()=>{app.selected=chart;renderList();renderDetail();};return button;
  }));
}

function renderDetail(){
  const chart=app.selected;if(!chart)return;
  const quote=quoteFor(chart),value=allocation(quote?.price,chart.lowerLine,chart.upperLine),detail=$('detail');detail.replaceChildren();
  const header=document.createElement('header');header.className='detail-header';const identity=document.createElement('div');identity.className='identity';identity.append(text('span','RISK / REWARD CHART','eyebrow'),text('h2',chart.displayTickerSymbol),text('p',chart.companyName));
  const currency=chart.currency||quote?.currency||'USD';const quoteBlock=document.createElement('div');quoteBlock.className='quote-block';quoteBlock.append(text('strong',money(quote?.price,currency)),text('span',quote?`${quote.provider||'Quote'} · ${currency} · ${dateTime(quote.quotedAt)}${quote.isStale?' · stale':''}`:'Current quote unavailable'));header.append(identity,quoteBlock);
  const metrics=document.createElement('div');metrics.className='metrics';metrics.append(metric('Suggested allocation',value==null?'—':`${value.toFixed(2)}%`),metric('Upper line',money(chart.upperLine,currency)),metric('Lower line',money(chart.lowerLine,currency)));
  detail.append(header,metrics);
  if(chart.upperLine&&chart.lowerLine){
    const card=document.createElement('div');card.className='range-card';const copy=document.createElement('div');copy.className='range-copy';copy.append(text('strong',value==null?'Awaiting price':`${value.toFixed(2)}% invested cash`),text('span',positionLabel(quote?.price,chart.lowerLine,chart.upperLine)));
    const track=document.createElement('div');track.className='range-track';if(value!=null){const marker=document.createElement('span');marker.className='range-marker';marker.style.left=`${(10-value)*10}%`;track.append(marker);}const labels=document.createElement('div');labels.className='range-labels';labels.append(text('span','10% · Lower risk'),text('span','0% · Higher risk'));card.append(copy,track,labels);detail.append(card);
  }
  const frame=document.createElement('div');frame.className='chart-frame';const image=document.createElement('img');image.src=chart.chartFilename;image.alt=`${chart.displayTickerSymbol} risk/reward chart`;image.onclick=()=>openLightbox(image);frame.append(image);detail.append(frame);
  const meta=document.createElement('div');meta.className='chart-meta';meta.append(text('span',`Chart updated ${chart.updatedDate||'—'}`),text('span','Allocation uses logarithmic price position'));detail.append(meta);if(chart.comments)detail.append(text('p',chart.comments,'comment'));
}

function metric(label,value){const node=document.createElement('div');node.className='metric';node.append(text('span',label),text('strong',value));return node;}
function positionLabel(price,lower,upper){if(!price)return 'Current position unavailable';if(price>=upper)return 'At or above upper line';if(price<=lower)return 'At or below lower line';return 'Inside risk/reward range';}
function openLightbox(image){$('lightboxImage').src=image.src;$('lightboxImage').alt=image.alt;$('lightbox').showModal();}
$('lightboxClose').onclick=()=>$('lightbox').close();$('lightbox').onclick=event=>{if(event.target===$('lightbox'))$('lightbox').close();};
$('search').addEventListener('input',event=>{app.query=event.target.value.trim().toLowerCase();renderList();});
load();setInterval(load,15*60*1000);

window.riskRewardHistory=(()=>{
  let manifestPromise;
  const manifest=()=>manifestPromise??=fetch(`history/manifest.json?v=${Date.now()}`,{cache:'no-store'}).then(response=>response.ok?response.json():{}).catch(()=>({}));
  const monthKey=value=>`${value.getUTCFullYear()}-${String(value.getUTCMonth()+1).padStart(2,'0')}.json`;
  async function load(symbol,from,to){
    const entries=await manifest(),entry=entries[String(symbol).toUpperCase()];if(!entry)return {symbol,availableSince:null,points:[]};
    const start=new Date(from),end=new Date(to),files=(entry.files||[]).filter(file=>file>=monthKey(start)&&file<=monthKey(end));
    const version=encodeURIComponent(entry.last||'');
    const results=await Promise.all(files.map(file=>fetch(`history/${encodeURIComponent(String(symbol).toUpperCase())}/${file}?v=${version}`).then(response=>response.ok?response.json():null).catch(()=>null)));
    const points=results.flatMap(file=>file?.points||[]).map(point=>({x:new Date(point[0]),y:Number(point[1])})).filter(point=>Number.isFinite(point.x.getTime())&&Number.isFinite(point.y)&&point.x>=start&&point.x<=end).sort((a,b)=>a.x-b.x);
    return {symbol,currency:entry.currency,exchange:entry.exchange,availableSince:entry.first||null,points};
  }
  return {load,has:async symbol=>Boolean((await manifest())[String(symbol).toUpperCase()])};
})();

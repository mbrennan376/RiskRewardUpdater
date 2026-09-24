(()=>{
  const button=document.getElementById('menuButton');if(!button)return;
  const overlay=document.createElement('div');overlay.className='nav-overlay';overlay.hidden=true;
  const drawer=document.createElement('aside');drawer.id='siteNavigation';drawer.className='site-navigation';drawer.setAttribute('aria-label','Site navigation');drawer.hidden=true;
  const heading=document.createElement('div');heading.className='nav-heading';const copy=document.createElement('div');copy.innerHTML='<span class="eyebrow">NAVIGATION</span><h2>Risk / Reward</h2>';
  const close=document.createElement('button');close.className='close-button';close.type='button';close.setAttribute('aria-label','Close navigation');close.textContent='×';heading.append(copy,close);
  const links=document.createElement('nav');drawer.append(heading,links);document.body.append(overlay,drawer);
  const open=()=>{overlay.hidden=false;drawer.hidden=false;requestAnimationFrame(()=>document.body.classList.add('navigation-open'));button.setAttribute('aria-expanded','true');close.focus();};
  const shut=()=>{document.body.classList.remove('navigation-open');button.setAttribute('aria-expanded','false');setTimeout(()=>{overlay.hidden=true;drawer.hidden=true;},180);button.focus();};
  button.addEventListener('click',open);close.addEventListener('click',shut);overlay.addEventListener('click',shut);document.addEventListener('keydown',event=>{if(event.key==='Escape'&&!drawer.hidden)shut();});
  fetch('menu.json',{cache:'no-store'}).then(response=>response.json()).then(menu=>{
    links.replaceChildren(...menu.map(item=>{const link=document.createElement('a');link.href=item.link;if(/^https?:/i.test(item.link)){link.target='_blank';link.rel='noopener';}const mark=document.createElement('span');mark.className='nav-mark';if(item.icon){const image=document.createElement('img');image.src=item.icon;image.alt='';mark.append(image);}else mark.textContent=item.title.slice(0,1);const label=document.createElement('span');label.textContent=item.title;link.append(mark,label);return link;}));
  }).catch(()=>{links.textContent='Navigation could not be loaded.';});
})();

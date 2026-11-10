(async () => {
  setRunning(false);
  const rate = 120;
  const clips = [
    {name:"Walk", length:2.4, at:t=>build(2,"hammer",Math.min(t,2.4-1e-6),"arrow")},
    {name:"Hammer combo", length:3.7+1/30,
      at:t=>build(2,"hammer",marchSeconds+Math.min(hammerPreviewSourceTime(t),attackSeconds-1e-6),"arrow")},
    ...["arrow","blade","crushing"].map(cause=>({
      name:"Death / "+cause, length:4, at:t=>build(2,"hammer",deathStart+t,cause)})),
    ...hammerReviewPoses.map((pose,i)=>({name:"Approved pose "+(i+1),length:0,
      at:()=>build(2,"hammer",marchSeconds+pose.time,"arrow",pose)}))
  ];
  const bytes = [];
  const buffer = new ArrayBuffer(4), view = new DataView(buffer);
  const u32 = n => {view.setUint32(0,n,true);bytes.push(...new Uint8Array(buffer));};
  const float = n => {
    if(!Number.isFinite(n))throw new Error("Nonfinite exported value");
    view.setFloat32(0,n,true);bytes.push(...new Uint8Array(buffer));
  };
  const text = s => {const b=new TextEncoder().encode(s);u32(b.length);bytes.push(...b);};
  const first=clips[0].at(0),parts=[...first.actorParts,...first.weaponParts];
  text("TermCity.OrcMotion.1");u32(rate);u32(parts.length);u32(clips.length);
  for(const p of parts){u32(p.color);text(p.owner||"weapon");text(p.tag||"detail");}
  let maxBoneError=0;
  for(const clip of clips){
    text(clip.name);float(clip.length);
    const count=Math.max(1,Math.ceil(clip.length*rate)+1);u32(count);
    for(let i=0;i<count;i++){
      const time=count===1?0:clip.length*i/(count-1),f=clip.at(time);
      const boxes=[...f.actorParts,...f.weaponParts];
      if(boxes.length!==parts.length)throw new Error(clip.name+": geometry count changes at "+time);
      float(time);
      for(let j=0;j<boxes.length;j++){
        const p=boxes[j],reference=parts[j],m=p.m;
        if(p.color!==reference.color||(p.owner||"weapon")!==(reference.owner||"weapon")||
          (p.tag||"detail")!==(reference.tag||"detail"))
          throw new Error(clip.name+": part identity changes at "+time+", "+j);
        const scales=[Math.hypot(...m.slice(0,3)),Math.hypot(...m.slice(4,7)),Math.hypot(...m.slice(8,11))];
        if(scales.some(s=>s<=0))throw new Error("Invalid part scale");
        const r=[...times(m.slice(0,3),1/scales[0]),0,...times(m.slice(4,7),1/scales[1]),0,
          ...times(m.slice(8,11),1/scales[2]),0,0,0,0,1];
        // Reflection remains in one signed scale, never an invalid quaternion.
        if(dot(cross(r.slice(0,3),r.slice(4,7)),r.slice(8,11))<0){
          scales[0]*=-1;for(let k=0;k<3;k++)r[k]*=-1;
        }
        for(const n of [m[12],m[13],m[14]-f.root[14],...quat(r),...scales])float(n);
      }
      for(const side of [1,-1]){
        const h=f.diagnostics.hands.find(h=>h.side===side),a=h.arm;
        const body=mul(f.root,f.body);
        for(const v of [a.shoulder,a.elbow,a.hand]){
          const p=point(body,v);for(const n of [p[0],p[1],p[2]-f.root[14]])float(n);
        }
        const palm=mul(f.root,h.palm),forward=norm(palm.slice(8,11));
        for(const n of forward)float(n);
        maxBoneError=Math.max(maxBoneError,Math.abs(Math.hypot(...sub(a.elbow,a.shoulder))-.56),
          Math.abs(Math.hypot(...sub(a.hand,a.elbow))-.56));
      }
    }
  }
  const raw=new Uint8Array(bytes);
  const compressed=await new Response(new Blob([raw]).stream().pipeThrough(new CompressionStream("gzip"))).arrayBuffer();
  const data=new Uint8Array(compressed);
  let encoded="";for(let i=0;i<data.length;i+=32768)encoded+=String.fromCharCode(...data.subarray(i,i+32768));
  return {data:btoa(encoded),parts:parts.length,clips:clips.length,rate,rawBytes:raw.length,
    compressedBytes:data.length,maxBoneError};
})()

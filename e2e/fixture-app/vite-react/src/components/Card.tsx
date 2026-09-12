export function Card({ id, children, ...props }: any) {
  return (
    <div id={id} className="card" {...props}>
      {children || 'Card component'}
    </div>
  );
}
